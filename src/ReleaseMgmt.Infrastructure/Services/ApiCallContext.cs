using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// REOS-162: the API token a request came in with, carried on the request's flow the way <see cref="ReleaseMgmt.Domain.Common.WorkspaceScope"/> carries the workspace.
/// A service write made under it keeps the person the token acts as in <c>ActorUserId</c> (the audit actor, and the user the foreign keys need) and names the token in the
/// row's <c>AfterJson</c> as <c>_via</c>, so a reader can tell a person's own click from their token's call. Rows that triggers write carry only the person (no service code runs there).
/// </summary>
public static class ApiCallContext
{
    public sealed record Token(string Id, string Name, string OwnerUserId);

    private static readonly AsyncLocal<Token?> Ambient = new();
    public static Token? Current => Ambient.Value;

    /// <summary>Synchronous on purpose, like <c>WorkspaceScope.Enter</c>: the value then flows to everything the caller awaits afterwards.</summary>
    public static IDisposable Enter(string tokenId, string tokenName, string ownerUserId)
    {
        var previous = Ambient.Value;
        Ambient.Value = new Token(tokenId, tokenName, ownerUserId);
        return new Exit(previous);
    }

    private sealed class Exit(Token? previous) : IDisposable
    {
        private bool _done;
        public void Dispose() { if (_done) return; _done = true; Ambient.Value = previous; }
    }

    /// <summary>The audit row's after-image with the token that made the call added; unchanged outside a token call.</summary>
    public static string? WithVia(string? afterJson)
    {
        if (Current is not { } t) return afterJson;
        var via = new JsonObject { ["apiTokenId"] = t.Id, ["apiToken"] = t.Name };
        JsonNode? node = afterJson is null ? null : JsonNode.Parse(afterJson);
        var obj = node as JsonObject ?? (node is null ? new JsonObject() : new JsonObject { ["value"] = node });
        obj["_via"] = via;
        return obj.ToJsonString(new JsonSerializerOptions());
    }
}
