# Connecting AI clients to the MCP server

AL Workbench exposes its template browser, workspace generator, Object
Explorer queries, and snippet library through a Model Context Protocol (MCP)
endpoint at `/mcp`. There are two supported sign-in styles, and which one
you pick depends on the assistant:

| Assistant                          | Auth method                  |
|------------------------------------|------------------------------|
| Claude on the web (claude.ai)      | OAuth (custom connector)     |
| Claude mobile                      | OAuth (custom connector)     |
| ChatGPT (custom connector)         | OAuth (custom connector)     |
| Claude Desktop                     | Personal access token        |
| Claude Code (CLI)                  | Personal access token        |
| Cursor                             | Personal access token        |
| VS Code Copilot agent mode         | Personal access token        |
| Microsoft 365 Copilot / Copilot Studio | Personal access token    |
| OpenWebUI                          | Personal access token        |

The in-app docs hub at **`/docs/mcp`** is the long-form, layman-friendly
walkthrough — link signed-in users there. This file is the same content in
markdown form so it shows up in the repo.

## Connect with AL Workbench (Claude on the web & mobile)

Claude.ai's **directory** and **custom connector** flow does not accept a
pasted bearer token — `static_bearer` is not in Anthropic's supported auth
matrix. Instead, AL Workbench runs a small OAuth 2.1 server (powered by
OpenIddict) at `/.well-known/oauth-authorization-server`, and Claude
registers itself via Dynamic Client Registration (RFC 7591) on first
connect.

1. Open Claude.ai → **Settings → Connectors → Add custom connector**.
2. Enter `https://YOUR-SERVER/mcp` as the URL. Leave the OAuth Client
   Secret field empty — DCR registers Claude as a public PKCE client.
3. Sign in to AL Workbench in the popup, click **Allow** on the
   permission screen, and you're connected.

To take access back, go to **Account → Connected assistants** at
`/account/ai`.

## Connect with AL Workbench (ChatGPT custom connectors)

ChatGPT's custom-connector flow uses the same OAuth 2.1 server, but the
client-authentication shape differs: instead of registering as a public
PKCE client, ChatGPT publishes a per-connector Client ID Metadata Document
(CIMD) at `https://chatgpt.com/oauth/<id>/client.json` and authenticates
the token endpoint by signing a JWT with a private key whose public half
lives at `https://chatgpt.com/oauth/jwks.json`. AL Workbench accepts
both shapes — `token_endpoint_auth_method=none` (Claude) and
`token_endpoint_auth_method=private_key_jwt` (ChatGPT) — and fetches the
JWKS on every authorize so a key rotation only requires the user to
reconnect, not an administrator to intervene.

1. Open ChatGPT → **Settings → Connectors → Add custom connector** (or
   the equivalent in your workspace's admin panel).
2. Enter `https://YOUR-SERVER/mcp` as the URL.
3. Sign in to AL Workbench in the popup, click **Allow** on the
   permission screen, and you're connected.

As with Claude, revoke access at **Account → Connected assistants**
(`/account/ai`). If ChatGPT rotates its signing keys mid-flight
the next refresh fails and ChatGPT will prompt you to reconnect — clicking
through re-fetches the JWKS and gets you back in.

## Personal access tokens (desktop & CLI)

### Create a token

1. Sign in at the AL Workbench web app.
2. Go to **Account → Manage access tokens** (or directly to
   `/account/ai`).
3. Click **Create token**. Give it a recognisable name (e.g. *Cursor on
   laptop*) and pick an expiry. **Save the token immediately** — the next
   page is the only place it will be shown.

You can revoke a token at any time from the same page. SiteAdmins can revoke
tokens across the whole deployment at `/site-admin/access-tokens`.

### Configure your AI client

The reveal screen at `/account/access-tokens/created` shows ready-to-paste
snippets with your token and the server URL pre-filled. The reference shapes
below use `${PAT}` and `${SERVER}` placeholders.

### VS Code (GitHub Copilot agent mode)

Save to your workspace's `.vscode/mcp.json` (or your user `settings.json`
under `mcp.servers`). Requires Copilot agent mode to be enabled.

```json
{
  "servers": {
    "aldevtoolbox": {
      "type": "http",
      "url": "${SERVER}/mcp",
      "headers": {
        "Authorization": "Bearer ${PAT}"
      }
    }
  }
}
```

Reload the window after saving.

### Claude Desktop

Open **Settings → Developer → Edit Config**. The config file is at:
- macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`
- Windows: `%APPDATA%\Claude\claude_desktop_config.json`
- Linux: `~/.config/Claude/claude_desktop_config.json`

```json
{
  "mcpServers": {
    "aldevtoolbox": {
      "type": "http",
      "url": "${SERVER}/mcp",
      "headers": {
        "Authorization": "Bearer ${PAT}"
      }
    }
  }
}
```

Restart Claude Desktop after saving.

### Cursor

Open **Cursor Settings → MCP → Add Server**, or edit `~/.cursor/mcp.json`
directly:

```json
{
  "mcpServers": {
    "aldevtoolbox": {
      "url": "${SERVER}/mcp",
      "headers": {
        "Authorization": "Bearer ${PAT}"
      }
    }
  }
}
```

Cursor reloads MCP servers automatically.

### Claude Code (CLI)

Run from any project directory:

```sh
claude mcp add aldevtoolbox \
  --transport http \
  --url ${SERVER}/mcp \
  --header "Authorization: Bearer ${PAT}"
```

The server is then available in any Claude Code session in that directory.

### Microsoft 365 Copilot / Copilot Studio

You author the connection in **Copilot Studio**; once you publish the agent
it surfaces in M365 Copilot. The connector runs from Microsoft's cloud, so
the AL Workbench deployment has to be reachable over HTTPS from the
public internet (same constraint as Claude.ai).

1. Open Copilot Studio, pick (or create) an agent, then go to
   **Tools → Add a tool → Model Context Protocol**.
2. Set the **Server URL** to `${SERVER}/mcp`.
3. Under **Authentication**, choose `API key`. Set the header name to
   `Authorization` and the value to `Bearer ${PAT}`.
4. Save the tool, then **Publish** the agent.

The agent acts as the user who issued the PAT — same organisation, same
role — regardless of which Copilot channel it's surfaced in.

### OpenWebUI

OpenWebUI's native MCP client lives under **Admin Panel → Settings → Tools
→ MCP Servers → Add server** (or per-user under **Settings → Tools** for a
personal connection). It accepts the same server shape as Claude Desktop:

```json
{
  "aldevtoolbox": {
    "type": "http",
    "url": "${SERVER}/mcp",
    "headers": {
      "Authorization": "Bearer ${PAT}"
    }
  }
}
```

OpenWebUI hot-reloads MCP servers. Pick a model that supports tool calling
— the workbench's tools only fire on tool-aware chat models.

## What you can ask

Once connected, the agent has tools for:

- **Templates and generation** — list templates and modules, generate a new
  workspace or a standalone extension. The generated ZIP comes back inline
  (base64) in the tool result.
- **Object Explorer** — list releases, search objects / procedures /
  content, and find references to a specific table or field across a BC
  release. Useful for *"which procedures touch the No. field on Sales
  Line in version 28.1?"*-style questions.
- **Forward-edge navigation** — outline an object, read one procedure's
  source, list its outgoing calls. The three tools chain naturally:
  `get_object_outline` returns each symbol's id and line number;
  `get_procedure_source` slices the body (capped at 200 lines with a
  truncation marker); `list_procedure_calls` returns the outgoing
  method calls and field accesses so the agent can follow the chain.
  Useful for *"what happens when you post a sales order in 28.1?"* —
  trace from the Sales Order page's Post action through
  `CallPostDocument` into Codeunit `Sales-Post`. Pass `symbolId` from
  the outline when the procedure name is ambiguous (page-action
  `OnAction`, table-field `OnValidate`).
- **Snippets** — search and fetch snippets your organisation has saved.

The agent acts as you: same organisation, same role, same audit trail. PATs
do **not** carry SiteAdmin authority unless your account does.

## Troubleshooting

- **401 Unauthorized** — the token is missing, malformed, expired, or
  revoked. Re-issue it.
- **The server doesn't appear in my client** — confirm the URL is reachable
  from your machine (`curl -H "Authorization: Bearer ${PAT}" ${SERVER}/mcp`
  should return a 200 / streamable response, not a 404 or HTML).
- **Tool calls return validation errors** — the workbench returns the same
  field-keyed errors the web UI uses. Re-prompt the agent with the missing
  field or a corrected value.
