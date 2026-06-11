# Claude — Alexa custom skill

Ask Alexa a question and have it answered by Anthropic's Claude. The user speaks a
question, the skill forwards it to the Anthropic API, and the plain-text reply is
spoken back.

- **Invocation name:** `claude`
- **Single intent:** `AskClaudeIntent` — captures a free-form question and answers it.
- **Backend:** C# .NET 8 Azure Function (HTTP trigger, isolated worker) with Alexa
  request-signature verification via the `Alexa.NET.Security.Functions` package.
- **Model:** `claude-sonnet-4-20250514`, `max_tokens` = 300, system prompt tuned for
  concise spoken answers (2–3 sentences, no markdown).

```
Alexa device  ──speech──▶  Alexa service  ──HTTPS POST──▶  Azure Function
                                                              │ verify signature
                                                              │ call Anthropic API
                                                              ▼
Alexa speaks  ◀──JSON response──────────────────────────  plain-text answer
```

> **Heads up — model retirement.** `claude-sonnet-4-20250514` is deprecated and is
> scheduled to retire on **2026-06-15**. After that date the API returns 404 for this
> model ID. The model is read from the `CLAUDE_MODEL` app setting, so you can swap it
> (e.g. to `claude-sonnet-4-6`) without touching code. See
> <https://platform.claude.com/docs/en/about-claude/models/overview> for current IDs.

---

## Files

| File | Purpose |
| --- | --- |
| `ClaudeSkillFunction.cs` | The HTTP-triggered function: signature verification, routing, Anthropic call. |
| `Program.cs` | .NET 8 isolated-worker host bootstrap. |
| `Claude.Answers.csproj` | Project + NuGet references. |
| `interaction-model.json` | The Alexa interaction model (invocation name, intent, slots, samples). |
| `host.json` / `local.settings.json` | Azure Functions configuration (local settings holds secrets, git-ignored). |

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- An **Azure account** (the Function must be reachable over public HTTPS).
- An **Amazon developer account** — <https://developer.amazon.com> (free; a developer
  account is all you need, no skill certification or publishing required).
- An **Anthropic API key** — <https://console.anthropic.com>.

---

## 1. Build and run locally

```bash
cd Claude.Answers
dotnet restore
dotnet build
```

> **NuGet versions:** the package versions in `Claude.Answers.csproj` are known-good at
> the time of writing. If `dotnet restore` can't resolve one, run
> `dotnet add package <Name>` to pull the latest compatible version (the
> `AnthropicClient` / `MessageCreateParams` and `Alexa.NET` request/response APIs used
> here are stable across recent releases).

Put your real key in `local.settings.json` (`ANTHROPIC_API_KEY`), then:

```bash
func start
```

The function listens at `http://localhost:7071/api/Claude`. Note that Alexa
**signature verification will reject local calls** that don't carry valid Alexa
signature headers — local `func start` is mainly for confirming the project builds and
boots. End-to-end testing happens against the deployed Azure URL using the Alexa
developer console simulator (below).

---

## 2. Deploy to Azure

Create the Function App (one-time), then publish. Example using the Azure CLI:

```bash
# Login and create resources (adjust names/region as needed)
az login
az group create --name claude-skill-rg --location eastus
az storage account create --name claudeskillstore$RANDOM --location eastus \
  --resource-group claude-skill-rg --sku Standard_LRS
az functionapp create --resource-group claude-skill-rg \
  --consumption-plan-location eastus --runtime dotnet-isolated \
  --functions-version 4 --name claude-answers-fn \
  --storage-account <the-storage-account-name>

# Configure secrets / settings
az functionapp config appsettings set --name claude-answers-fn \
  --resource-group claude-skill-rg \
  --settings ANTHROPIC_API_KEY="sk-ant-..." CLAUDE_MODEL="claude-sonnet-4-20250514"

# Publish the code
func azure functionapp publish claude-answers-fn
```

After publishing, your endpoint is:

```
https://claude-answers-fn.azurewebsites.net/api/Claude
```

Azure provides a valid TLS certificate on `*.azurewebsites.net`, which satisfies
Alexa's HTTPS requirement out of the box.

> **Security note:** the function uses `AuthorizationLevel.Anonymous` because Alexa
> cannot send an Azure function key, and **all** authenticity is enforced by Alexa
> request-signature verification plus the 150-second timestamp check inside the
> function. This is the standard pattern for Alexa skills on Azure Functions.

---

## 3. Create the skill in the Alexa developer console

1. Go to <https://developer.amazon.com/alexa/console/ask> and sign in with your Amazon
   developer account.
2. Click **Create Skill**.
   - **Skill name:** `Claude` (or anything you like).
   - **Primary locale:** English (or your preferred locale).
   - **Experience / model:** choose **Custom**.
   - **Hosting:** choose **Provision your own** (you are hosting on Azure, not on
     Alexa-hosted).
   - Start from a **blank/scratch** template.
3. **Invocation name:** open **Build ▸ Invocation** and set it to `claude`.
4. **Interaction model:** open **Build ▸ Interaction Model ▸ JSON Editor**, paste the
   contents of [`interaction-model.json`](./interaction-model.json), and click
   **Save Model**, then **Build Model**.
5. **Endpoint:** open **Build ▸ Endpoint**.
   - Select **HTTPS**.
   - **Default Region:** paste your Azure Function URL:
     `https://claude-answers-fn.azurewebsites.net/api/Claude`
   - For the SSL certificate type, choose **“My development endpoint is a sub-domain
     of a domain that has a wildcard certificate from a certificate authority.”**
     (`*.azurewebsites.net` is exactly that.)
   - **Save Endpoints.**

---

## 4. Test it

Open the **Test** tab, enable testing for **Development**, and try in the simulator
(typed or spoken):

- “open claude”
- “ask claude why is the sky blue”
- “ask claude to tell me a fact about octopuses”

You should hear a short, spoken answer from Claude.

---

## Developer account only — no certification needed

This skill is intended for **personal/development use**. Once your interaction model is
built and the endpoint is set, the skill is immediately usable in the **Development**
stage on any Echo device or the Alexa app signed in with the **same Amazon account** as
your developer account. You do **not** need to submit the skill for certification or
publish it to the Alexa Skills Store. (Certification is only required if you want to
distribute the skill publicly to other users.)

---

## How request verification works

Every inbound request is validated before any work is done:

1. **Signature verification** — `Alexa.NET.Security.Functions.RequestVerification.Verify`
   downloads and validates the Amazon signing certificate referenced by the
   `SignatureCertChainUrl` header and checks the `Signature` header against the raw
   request body.
2. **Timestamp check** — the request `timestamp` must be within 150 seconds of now,
   which blocks replayed requests.

Requests that fail either check get a `400 Bad Request` and never reach the Anthropic
API.
