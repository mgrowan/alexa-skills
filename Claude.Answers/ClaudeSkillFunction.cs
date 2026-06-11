using System.Net;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Security.Functions;
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
/// Anthropic's API, and speaks back the plain-text answer.
/// </summary>
public class ClaudeSkillFunction
{
    // The voice persona: short, spoken-friendly answers with no markdown.
    private const string SystemPrompt =
        "You are Claude, answering through the Alexa voice assistant. " +
        "Keep every answer to 2-3 short sentences suitable for being read aloud. " +
        "Use plain conversational text only: no markdown, no bullet points, no headings, " +
        "no code blocks, no emoji, and no special symbols. If you don't know, say so briefly.";

    private const int MaxTokens = 300;

    // Alexa requires the request timestamp to be within 150 seconds of now.
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(150);

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
        _model = Environment.GetEnvironmentVariable("CLAUDE_MODEL") ?? "claude-sonnet-4-20250514";
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
        switch (intentRequest.Intent.Name)
        {
            case "AskClaudeIntent":
                var question = intentRequest.Intent.Slots?.GetValueOrDefault("question")?.Value;
                if (string.IsNullOrWhiteSpace(question))
                {
                    return ResponseBuilder.Ask(
                        Speech("I didn't catch a question. What would you like to ask?"),
                        new Reprompt("What's your question?"));
                }

                var answer = await AskClaude(question);
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
                Messages = [new() { Role = Role.User, Content = question }]
            };

            var response = await _anthropic.Messages.Create(parameters);

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
            return await RequestVerification.Verify(signature, certUri, body);
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
