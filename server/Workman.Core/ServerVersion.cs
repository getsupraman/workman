namespace Workman.Core;

/// <summary>
/// The release this server is: the MCP <c>serverInfo.version</c> (ApiHost) and the <c>server</c> line of
/// every brief both read it, so the two cannot name different releases. A release bumps it together
/// with CHANGELOG.md's newest version heading, which ServerVersionTests holds them to.
/// </summary>
public static class ServerVersion
{
    public const string Text = "3.2.3";
}
