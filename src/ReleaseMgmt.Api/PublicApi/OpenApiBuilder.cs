using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Api.PublicApi;

/// <summary>
/// The OpenAPI 3.1 document of the public API (REOS-161), generated from <see cref="PublicApiCatalog"/> and the request records the endpoints take, so it cannot drift from the code by hand.
/// The committed copy is <c>docs/api/public-v1.openapi.json</c>; <c>PublicApiContractTests</c> fails when the two differ (regenerate with the command in that file's test) and when the catalog
/// and the mapped routes differ. Response bodies are described as the internal endpoints return them (objects or lists); the shared members are the problem details and the cursor page.
/// </summary>
public static class OpenApiBuilder
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static string? _cached;

    public static string Json() => _cached ??= Build().ToJsonString(Pretty) + "\n";

    private static JsonObject Ref(string name) => new() { ["$ref"] = "#/components/schemas/" + name };

    public static JsonObject Build()
    {
        var schemas = new JsonObject();
        var paths = new JsonObject();
        foreach (var op in PublicApiCatalog.All)
        {
            var item = paths[op.Path] as JsonObject ?? (JsonObject)(paths[op.Path] = new JsonObject());
            item[op.Method.ToLowerInvariant()] = Operation(op, schemas);
        }
        schemas["Problem"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "RFC 9457 problem details. `guard` names the rule that refused the request (it is the same name the internal API uses, `DbRule` for a database rule, `ScopeDenied`, `RateLimited` and so on) and `message` repeats `detail`; members such as `items`, `failures` and, on a 409, `current` (the row as it is now) are kept as the internal API returns them.",
            ["required"] = new JsonArray("type", "title", "status", "guard"),
            ["properties"] = new JsonObject
            {
                ["type"] = new JsonObject { ["type"] = "string", ["format"] = "uri", ["description"] = "urn:releaseos:problem:{guard}" },
                ["title"] = new JsonObject { ["type"] = "string" },
                ["status"] = new JsonObject { ["type"] = "integer" },
                ["detail"] = new JsonObject { ["type"] = "string" },
                ["instance"] = new JsonObject { ["type"] = "string", ["description"] = "The request path." },
                ["guard"] = new JsonObject { ["type"] = "string" },
                ["message"] = new JsonObject { ["type"] = "string" },
                ["requiredScope"] = new JsonObject { ["type"] = "string", ["description"] = "On a 403 ScopeDenied: the scope the operation needs." },
                ["current"] = new JsonObject { ["description"] = "On a 409 VersionConflict: the row as it is now." },
            },
            ["additionalProperties"] = true,
        };
        schemas["Page"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("data", "page"),
            ["properties"] = new JsonObject
            {
                ["data"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } },
                ["page"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("limit", "hasMore", "nextCursor"),
                    ["properties"] = new JsonObject
                    {
                        ["limit"] = new JsonObject { ["type"] = "integer" },
                        ["hasMore"] = new JsonObject { ["type"] = "boolean" },
                        ["nextCursor"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["description"] = "Pass as `cursor` with the same filters to get the next page; null on the last page." },
                    },
                },
            },
        };
        var sortedSchemas = new JsonObject();
        foreach (var k in schemas.Select(x => x.Key).Order(StringComparer.Ordinal)) sortedSchemas[k] = schemas[k]!.DeepClone();

        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "ReleaseOS public API",
                ["version"] = PublicApiCatalog.Version,
                ["summary"] = "Releases, gates, approvals, calendar, deployments and analytics.",
                ["description"] = "Workspace-scoped and role-checked exactly like the application: a token acts as the person who made it (or last rotated it), in the token's workspace, and its scopes narrow what that person's role allows. "
                    + "Status changes are service actions (`:advance`, `:complete`, `:certify`, …); nothing sets a status column. Writes that change a record need `If-Match` carrying the record's `version`. "
                    + "A POST may carry an `Idempotency-Key`; a repeat of a successful request returns the stored result. Versioning and deprecation: docs/API_VERSIONING.md.",
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = PublicApiCatalog.Prefix }),
            ["security"] = new JsonArray(new JsonObject { ["bearerToken"] = new JsonArray() }),
            ["tags"] = new JsonArray([.. PublicApiCatalog.All.Select(o => o.Tag).Distinct().Order(StringComparer.Ordinal).Select(t => (JsonNode?)new JsonObject { ["name"] = t })]),
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["bearerToken"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer", ["description"] = "An API token (`rt_…`) from Admin, API tokens. Shown once when it is made or rotated." },
                },
                ["schemas"] = sortedSchemas,
            },
        };
    }

    private static JsonObject ErrorResponse(string description) => new()
    {
        ["description"] = description,
        ["content"] = new JsonObject { ["application/problem+json"] = new JsonObject { ["schema"] = Ref("Problem") } },
    };

    private static JsonObject Operation(PublicOperation op, JsonObject schemas)
    {
        var o = new JsonObject
        {
            ["operationId"] = op.OperationId,
            ["summary"] = op.Summary,
            ["tags"] = new JsonArray(op.Tag),
            ["x-required-scope"] = op.Scope,
            ["description"] = $"Needs the `{op.Scope}` scope." + (op.Note is null ? "" : " " + op.Note),
        };
        var ps = new JsonArray();
        foreach (var name in op.PathParameters)
            ps.Add(new JsonObject { ["name"] = name, ["in"] = "path", ["required"] = true, ["schema"] = new JsonObject { ["type"] = "string" } });
        foreach (var q in op.Query ?? [])
            ps.Add(new JsonObject { ["name"] = q.Name, ["in"] = "query", ["required"] = q.Required, ["description"] = q.Description, ["schema"] = new JsonObject { ["type"] = q.Type } });
        if (op.Paged)
        {
            ps.Add(new JsonObject { ["name"] = "limit", ["in"] = "query", ["description"] = "Page size, 1 to 200; default 50.", ["schema"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 200 } });
            ps.Add(new JsonObject { ["name"] = "cursor", ["in"] = "query", ["description"] = "The `page.nextCursor` of the previous page, with the same filters.", ["schema"] = new JsonObject { ["type"] = "string" } });
        }
        if (op.IfMatch)
            ps.Add(new JsonObject { ["name"] = "If-Match", ["in"] = "header", ["required"] = true, ["description"] = "The `version` of the record, as you read it. A newer version is a 409 with the record as it is now; none is a 428.", ["schema"] = new JsonObject { ["type"] = "string" } });
        if (op.Method == "POST")
            ps.Add(new JsonObject { ["name"] = "Idempotency-Key", ["in"] = "header", ["description"] = "1 to 255 printable characters. A repeat of a successful request with the same key returns its stored result (header `Idempotent-Replayed: true`) and runs nothing.", ["schema"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 } });
        if (ps.Count > 0) o["parameters"] = ps;
        if (op.Body is not null)
            o["requestBody"] = new JsonObject
            {
                ["required"] = true,
                ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = SchemaOf(op.Body, schemas) } },
            };
        var ok = new JsonObject { ["description"] = op.Paged ? "A page of the list." : "The result." };
        ok["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = op.Paged ? Ref("Page") : new JsonObject { ["type"] = new JsonArray("object", "array") } } };
        var responses = new JsonObject { [op.Success.ToString()] = ok };
        responses["400"] = ErrorResponse("The request could not be read (a bad cursor, limit or Idempotency-Key, or a malformed body).");
        responses["401"] = ErrorResponse("The token is missing, unknown, revoked or expired.");
        responses["403"] = ErrorResponse($"The token lacks the `{op.Scope}` scope (`ScopeDenied`, `requiredScope` names it), or the person it acts as lacks the role the operation needs (`RoleDenied`).");
        responses["404"] = ErrorResponse("The record does not exist in the token's workspace.");
        if (op.Method == "POST")
        {
            responses["409"] = ErrorResponse("The record changed since you read it (`VersionConflict`, with `current`), or the first request with this Idempotency-Key is still running.");
            responses["422"] = ErrorResponse("A rule refused the request (`guard` names it; `DbRule` is a database rule), or the Idempotency-Key was used for another request.");
            if (op.IfMatch) responses["428"] = ErrorResponse("`If-Match` is missing (`PreconditionRequired`).");
        }
        responses["429"] = ErrorResponse("Too many requests for the token or its workspace; wait `Retry-After` seconds. `X-RateLimit-Limit`, `X-RateLimit-Remaining` and `X-RateLimit-Reset` are on every response.");
        o["responses"] = responses;
        return o;
    }

    // ---- request bodies: JSON schema from the record the endpoint binds ----------------------------------------------------------

    private static JsonObject SchemaOf(Type t, JsonObject schemas)
    {
        if (!schemas.ContainsKey(t.Name))
        {
            schemas[t.Name] = new JsonObject();   // guards a record that refers to itself
            var props = new JsonObject();
            var required = new JsonArray();
            var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            var nullability = new NullabilityInfoContext();
            foreach (var p in ctor.GetParameters())
            {
                var name = JsonNamingPolicy.CamelCase.ConvertName(p.Name!);
                var info = nullability.Create(p);
                var nullable = info.ReadState == NullabilityState.Nullable || Nullable.GetUnderlyingType(p.ParameterType) is not null;
                props[name] = TypeSchema(Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType, schemas);
                if (!nullable && !p.HasDefaultValue) required.Add(name);
            }
            var s = new JsonObject { ["type"] = "object", ["properties"] = props };
            if (required.Count > 0) s["required"] = required;
            schemas[t.Name] = s;
        }
        return Ref(t.Name);
    }

    private static JsonNode TypeSchema(Type t, JsonObject schemas)
    {
        if (t == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (t == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (t == typeof(int) || t == typeof(long)) return new JsonObject { ["type"] = "integer" };
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal)) return new JsonObject { ["type"] = "number" };
        if (t == typeof(DateTime)) return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        if (t == typeof(DateOnly)) return new JsonObject { ["type"] = "string", ["format"] = "date" };
        if (t.IsGenericType && t.GetGenericArguments() is [var item] && typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
            return new JsonObject { ["type"] = "array", ["items"] = TypeSchema(Nullable.GetUnderlyingType(item) ?? item, schemas) };
        return t.IsClass || t.IsValueType && !t.IsPrimitive && !t.IsEnum ? SchemaOf(t, schemas) : new JsonObject();
    }
}
