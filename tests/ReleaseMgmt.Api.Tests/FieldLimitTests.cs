using System.Collections;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-66 (SEC-D7, Q-SEC-D1): every string field of every JSON body the API accepts has a maximum length, enforced before the handler runs:
/// an over-long value is 422 <c>{guard:"FieldTooLong", field, max}</c> and nothing is written. The write routes are enumerated from the running app
/// (as <see cref="EndpointRoleMatrixTests"/> does), so a new route or field without a limit fails here.
/// </summary>
public class FieldLimitTests
{
    /// <summary>Not under /api/v1 (so not behind the filter): the Development-only sign-in, whose e-mail and name go to UserProvisioner.</summary>
    private static readonly string[] Excluded = ["POST /auth/dev-login"];

    private sealed record Field(string Path, int Max, Func<string, JsonObject> Body);

    private static string KeyOf(RouteEndpoint e) => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault()} {e.RoutePattern.RawText}";

    /// <summary>Every write route with a JSON body, and the body's type.</summary>
    private static List<(RouteEndpoint E, Type Body)> JsonWrites(ApiFactory f)
    {
        f.CreateClient();
        return [.. f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Any(m => m is "POST" or "PUT" or "PATCH") == true)
            .Select(e => (E: e, Accepts: e.Metadata.GetMetadata<IAcceptsMetadata>()))
            .Where(x => x.Accepts?.RequestType is not null && x.Accepts.ContentTypes.Contains("application/json") && !Excluded.Contains(KeyOf(x.E)))
            .Select(x => (x.E, x.Accepts!.RequestType!))
            .OrderBy(x => KeyOf(x.E), StringComparer.Ordinal)];
    }

    private static string Json(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    private static Type? ElementOf(Type t) =>
        t == typeof(string) ? null : t.IsArray ? t.GetElementType() : t.GetInterfaces().Append(t).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];

    private static bool IsLeaf(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(decimal) || t.IsValueType || t == typeof(object);

    /// <summary>Walks a body type's string fields (into nested bodies and lists). Missing limits are collected in <paramref name="missing"/>.</summary>
    private static IEnumerable<Field> Fields(Type type, List<string> missing, string prefix = "")
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            var name = Json(p.Name);
            if (t == typeof(string) || ElementOf(t) == typeof(string))
            {
                var list = t != typeof(string);
                if (FieldLimits.Max(type, p.Name) is not int max) { missing.Add($"{type.FullName}.{p.Name}"); continue; }
                yield return new(list ? $"{prefix}{name}[0]" : prefix + name, max,
                    v => new JsonObject { [name] = list ? new JsonArray(JsonValue.Create(v)) : JsonValue.Create(v) });
            }
            else if (ElementOf(t) is { } el && !IsLeaf(el))
            {
                if (!FieldLimits.Covers(el)) { missing.Add($"{el.FullName} (in {type.FullName}.{p.Name})"); continue; }
                foreach (var inner in Fields(el, missing, $"{prefix}{name}[0]."))
                    yield return inner with { Body = v => new JsonObject { [name] = new JsonArray(inner.Body(v)) } };
            }
            else if (!IsLeaf(t) && t != typeof(JsonElement) && t.Namespace?.StartsWith("ReleaseMgmt", StringComparison.Ordinal) == true)
            {
                if (!FieldLimits.Covers(t)) { missing.Add($"{t.FullName} (in {type.FullName}.{p.Name})"); continue; }
                foreach (var inner in Fields(t, missing, $"{prefix}{name}."))
                    yield return inner with { Body = v => new JsonObject { [name] = inner.Body(v) } };
            }
        }
    }

    private static string Url(RouteEndpoint e) => Regex.Replace(e.RoutePattern.RawText!, @"\{\*?(\w+)(:[^}]*)?\}", m => m.Groups[1].Value switch
    {
        "day" => "2026-01-01", "source" => "Jira", "clientId" => "tab-12345678", _ => "x-1",
    });

    /// <summary>A hash of every row of every table, to prove a refused request wrote nothing.</summary>
    private static string Snapshot(ApiFactory f)
    {
        using var c = new SqliteConnection($"Data Source={f.DbPath};Mode=ReadOnly;Pooling=False");
        c.Open();
        var tables = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var t in tables)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{t}\" ORDER BY rowid";
            if (t == "__EFMigrationsHistory") cmd.CommandText = $"SELECT * FROM \"{t}\"";
            using var r = cmd.ExecuteReader();
            sha.AppendData(Encoding.UTF8.GetBytes("\n#" + t));
            while (r.Read())
                for (var i = 0; i < r.FieldCount; i++) sha.AppendData(Encoding.UTF8.GetBytes(Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) + "\u001f"));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    [Fact]
    public void Every_string_field_of_every_JSON_write_route_has_a_limit()
    {
        using var f = new ApiFactory();
        var routes = JsonWrites(f);
        var missing = new List<string>();
        var uncovered = routes.Where(r => !FieldLimits.Covers(r.Body) && HasStrings(r.Body)).Select(r => $"{KeyOf(r.E)}: {r.Body.FullName}").ToList();
        foreach (var (_, body) in routes) _ = Fields(body, missing).ToList();
        Assert.True(uncovered.Count == 0, "Body types with string fields and no row in FieldLimits (add them):\n  " + string.Join("\n  ", uncovered));
        Assert.True(missing.Count == 0, "String fields with no limit in FieldLimits:\n  " + string.Join("\n  ", missing.Distinct()));
        Assert.True(routes.Count >= 50, $"Only {routes.Count} JSON write routes were found; the enumeration is broken");
        var stale = FieldLimits.Types.Except(routes.SelectMany(r => Reachable(r.Body))).Select(t => t.FullName).ToList();
        Assert.True(stale.Count == 0, "FieldLimits rows for types no route accepts:\n  " + string.Join("\n  ", stale));
    }

    private static bool HasStrings(Type t) => t.GetProperties().Any(p =>
    {
        var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        return pt == typeof(string) || ElementOf(pt) is { } el && (el == typeof(string) || !IsLeaf(el)) || !IsLeaf(pt) && pt.Namespace?.StartsWith("ReleaseMgmt", StringComparison.Ordinal) == true;
    });

    private static IEnumerable<Type> Reachable(Type t) =>
        new[] { t }.Concat(t.GetProperties().Select(p => ElementOf(p.PropertyType) ?? p.PropertyType).Where(FieldLimits.Covers).Where(x => x != t).SelectMany(Reachable));

    [Fact]
    public async Task An_over_long_value_in_any_field_of_any_JSON_write_route_is_422_FieldTooLong_and_writes_nothing()
    {
        using var f = new ApiFactory();
        var routes = JsonWrites(f);
        var clients = new[] { await As(f, Roles.ReleaseManager, "rm@limits.test"), await As(f, Roles.GovernanceOfficer, "go@limits.test"), await As(f, Roles.Viewer, "v@limits.test") };
        var bodyLimit = 1024 * 1024;   // Limits:MaxRequestBodyBytes default: a limit above it is enforced by the 413 first

        var before = Snapshot(f);
        var wrong = new List<string>();
        var checks = 0;
        foreach (var (e, type) in routes)
        {
            var method = e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods[0];
            foreach (var field in Fields(type, []))
            {
                var json = field.Body(new string('x', field.Max + 1)).ToJsonString();
                HttpResponseMessage? res = null;
                foreach (var c in clients)   // the first role the route lets through (authorization runs before the filter)
                {
                    res?.Dispose();
                    res = await c.SendAsync(new HttpRequestMessage(new HttpMethod(method), Url(e)) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
                    if (res.StatusCode != HttpStatusCode.Forbidden) break;
                }
                checks++;
                var text = await res!.Content.ReadAsStringAsync();
                var tooBig = Encoding.UTF8.GetByteCount(json) > bodyLimit;
                if (tooBig)
                {
                    if (res.StatusCode != HttpStatusCode.RequestEntityTooLarge) wrong.Add($"{KeyOf(e)} {field.Path} (max {field.Max:N0}, over the body limit): {(int)res.StatusCode} {text[..Math.Min(200, text.Length)]}");
                    continue;
                }
                if (res.StatusCode != HttpStatusCode.UnprocessableEntity) { wrong.Add($"{KeyOf(e)} {field.Path}: {(int)res.StatusCode} {text[..Math.Min(200, text.Length)]}"); continue; }
                var body = JsonDocument.Parse(text).RootElement;
                if (body.GetProperty("guard").GetString() != "FieldTooLong" || body.GetProperty("field").GetString() != field.Path || body.GetProperty("max").GetInt32() != field.Max)
                    wrong.Add($"{KeyOf(e)} {field.Path}: {text[..Math.Min(200, text.Length)]}");
            }
        }
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {checks} over-long fields were not refused as expected:\n  " + string.Join("\n  ", wrong));
        Assert.True(checks >= 140, $"Only {checks} fields were checked; the enumeration is broken");
        Assert.Equal(before, Snapshot(f));   // nothing was written by any refused request

        // At the limit the filter lets the value through (the handler may still refuse it for its own reasons).
        foreach (var (e, type) in routes)
        {
            var method = e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods[0];
            foreach (var field in Fields(type, []).Where(x => x.Max < 100_000))
            {
                var json = field.Body(new string('x', field.Max)).ToJsonString();
                using var res = await clients[0].SendAsync(new HttpRequestMessage(new HttpMethod(method), Url(e)) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
                if (res.StatusCode == HttpStatusCode.UnprocessableEntity && (await res.Content.ReadAsStringAsync()).Contains("\"FieldTooLong\""))
                    wrong.Add($"{KeyOf(e)} {field.Path}: a value of exactly {field.Max} was refused");
            }
        }
        Assert.True(wrong.Count == 0, string.Join("\n  ", wrong));
    }

    [Fact]
    public async Task A_nested_or_listed_value_names_its_path_and_the_first_bad_field_wins()
    {
        using var f = new ApiFactory();
        var rm = await As(f, Roles.ReleaseManager, "rm@limits.test");
        var r = await rm.PostAsync("/api/v1/trains/t1/gonogo", new StringContent(JsonSerializer.Serialize(new
        {
            decision = "Go", notes = "fine",
            conditions = new object[] { new { text = "ok", ownerUserId = "u1" }, new { text = new string('c', 2001), ownerUserId = "u1" } },
        }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(("FieldTooLong", "conditions[1].text", 2000), (body.GetProperty("guard").GetString(), body.GetProperty("field").GetString(), body.GetProperty("max").GetInt32()));
        Assert.Contains("2,001", body.GetProperty("message").GetString());

        var team = await rm.PostAsync("/api/v1/teams", new StringContent(JsonSerializer.Serialize(new { handle = "ops", name = "Ops", memberIds = new[] { "a", new string('i', 65) } }), Encoding.UTF8, "application/json"));
        Assert.Equal("memberIds[1]", JsonDocument.Parse(await team.Content.ReadAsStringAsync()).RootElement.GetProperty("field").GetString());
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM Teams WHERE Handle='ops'"));
    }
}
