using System.Net;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Claude.Answers;

/// <summary>
/// HTTP-triggered Azure Function that backs the "Claude" Alexa custom skill.
/// It verifies the Alexa request signature, sends the user's question to
/// Anthropic's API (with web search enabled), and speaks back the answer.
/// </summary>
public class ClaudeSkillFunction
{
    // The voice persona: short, sharp, direct answers with no markdown.
    private const string SystemPrompt =
        "You are Claude, answering through the Alexa voice assistant. " +
        "The question may arrive with its first word or two dropped by speech recognition " +
        "(for example \"much did the company raise\" or \"many episodes are there\"); " +
        "infer the user's intended question and answer that. " +
        "When the answer depends on current events, recent facts, prices, schedules, or anything " +
        "time-sensitive, use web search before answering instead of relying on memory. " +
        "Answer fast: do at most one quick search, then answer. " +
        "Give the direct answer only, in one or two short spoken sentences. " +
        "No preamble, no restating the question, no summary, no sign-off, and no filler words. " +
        "Plain spoken text only: no markdown, lists, headings, code, emoji, citations, or special symbols. " +
        "If you still don't know, say so in a few words.";

    private const int MaxTokens = 300;

    // Alexa requires the request timestamp to be within 150 seconds of now.
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(150);

    // Alexa discards the response if the skill takes longer than ~8s. Bail out of
    // a slow Claude call before that so the user gets a graceful retry prompt.
    private static readonly TimeSpan ResponseBudget = TimeSpan.FromSeconds(7);

    private readonly ILogger _logger;
    private readonly AnthropicClient _anthropic;
    private readonly string _model;

    public ClaudeSkillFunction(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<ClaudeSkillFunction>();
        _anthropic = new AnthropicClient
        {
            ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
        };
        _model = Environment.GetEnvironmentVariable("CLAUDE_MODEL") ?? "claude-sonnet-4-6";
    }

    [Function("Claude")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        var body = await new StreamReader(req.Body).ReadToEndAsync();

        // 1. Verify the request really came from Alexa (cert chain + signature).
        if (!await IsValidAlexaRequest(req, body))
        {
            _logger.LogWarning("Rejected request: failed Alexa signature verification.");
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        SkillRequest? skillRequest;
        try
        {
            skillRequest = JsonConvert.DeserializeObject<SkillRequest>(body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Rejected request: body was not a valid Alexa skill request.");
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        if (skillRequest?.Request == null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        // 2. Reject stale/replayed requests outside Alexa's 150-second window.
        var skew = DateTime.UtcNow - skillRequest.Request.Timestamp.ToUniversalTime();
        if (skew.Duration() > TimestampTolerance)
        {
            _logger.LogWarning("Rejected request: timestamp outside tolerance ({Skew}).", skew);
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        // 3. Route the request and build a spoken response.
        var skillResponse = await HandleRequest(skillRequest);
        return await WriteJson(req, skillResponse);
    }

    private async Task<SkillResponse> HandleRequest(SkillRequest skillRequest)
    {
        _logger.LogInformation("Alexa request: {RequestType}", skillRequest.Request.Type);

        switch (skillRequest.Request)
        {
            case LaunchRequest:
                return ResponseBuilder.Ask(
                    Speech("Hi, this is Claude. What would you like to ask?"),
                    new Reprompt("What's your question?"));

            case IntentRequest intentRequest:
                return await HandleIntent(intentRequest);

            case SessionEndedRequest:
                return ResponseBuilder.Empty();

            default:
                return ResponseBuilder.Tell(Speech("Sorry, I couldn't handle that request."));
        }
    }

    private async Task<SkillResponse> HandleIntent(IntentRequest intentRequest)
    {
        _logger.LogInformation("Intent: {Intent}", intentRequest.Intent.Name);

        switch (intentRequest.Intent.Name)
        {
            case "AskClaudeIntent":
                var question = intentRequest.Intent.Slots?.GetValueOrDefault("question")?.Value;
                if (string.IsNullOrWhiteSpace(question))
                {
                    _logger.LogInformation("AskClaudeIntent received with no question slot value.");
                    return ResponseBuilder.Ask(
                        Speech("I didn't catch a question. What would you like to ask?"),
                        new Reprompt("What's your question?"));
                }

                _logger.LogInformation("Question: {Question}", question);
                var answer = await AskClaude(question);
                _logger.LogInformation("Answer: {Answer}", answer);
                return ResponseBuilder.Tell(Speech(answer));

            case "AMAZON.HelpIntent":
                return ResponseBuilder.Ask(
                    Speech("Just ask me anything and I'll answer. For example, why is the sky blue?"),
                    new Reprompt("What would you like to ask?"));

            case "AMAZON.StopIntent":
            case "AMAZON.CancelIntent":
                return ResponseBuilder.Tell(Speech("Goodbye."));

            default:
                return ResponseBuilder.Tell(Speech("Sorry, I'm not sure how to help with that."));
        }
    }

    private async Task<string> AskClaude(string question)
    {
        try
        {
            var parameters = new MessageCreateParams
            {
                Model = _model,
                MaxTokens = MaxTokens,
                System = SystemPrompt,
                // One server-side web search keeps current-events answers within Alexa's
                // ~8s response window; more searches reliably blow past it.
                Tools = [new WebSearchTool20260209 { MaxUses = 1 }],
                Messages = [new() { Role = Role.User, Content = question }]
            };

            var createTask = _anthropic.Messages.Create(parameters);

            // Don't let a slow lookup run past Alexa's timeout; return a retry prompt instead.
            if (await Task.WhenAny(createTask, Task.Delay(ResponseBudget)) != createTask)
            {
                _logger.LogWarning("Claude call exceeded the {Seconds}s budget; returning retry prompt.",
                    ResponseBudget.TotalSeconds);
                return "Sorry, that took too long to look up. Please ask again.";
            }

            var response = await createTask;

            // Concatenate the final text blocks (web search / tool-use blocks are ignored).
            var text = string.Concat(
                response.Content
                    .Select(block => block.Value)
                    .OfType<TextBlock>()
                    .Select(t => t.Text)).Trim();

            return string.IsNullOrWhiteSpace(text)
                ? "Sorry, I didn't have an answer for that."
                : text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Anthropic API call failed.");
            return "Sorry, I had trouble reaching Claude just now. Please try again.";
        }
    }

    private static async Task<bool> IsValidAlexaRequest(HttpRequestData req, string body)
    {
        if (!req.Headers.TryGetValues("SignatureCertChainUrl", out var certUrls) ||
            !req.Headers.TryGetValues("Signature", out var signatures))
        {
            return false;
        }

        var certUrl = certUrls.FirstOrDefault();
        var signature = signatures.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(certUrl) || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        if (!Uri.TryCreate(certUrl, UriKind.Absolute, out var certUri))
        {
            return false;
        }

        try
        {
            // Fully qualified: Alexa.NET.Request also defines a RequestVerification type.
            return await Alexa.NET.Security.RequestVerification.Verify(signature, certUri, body);
        }
        catch
        {
            return false;
        }
    }

    private static PlainTextOutputSpeech Speech(string text) => new() { Text = text };

    private static async Task<HttpResponseData> WriteJson(HttpRequestData req, SkillResponse skillResponse)
    {
        var http = req.CreateResponse(HttpStatusCode.OK);
        http.Headers.Add("Content-Type", "application/json");
        await http.WriteStringAsync(JsonConvert.SerializeObject(skillResponse));
        return http;
    }
}
