using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Project_no._1.Services;

public interface IGroqService
{
    Task<string> CompleteAsync(IReadOnlyList<GroqMessage> messages, CancellationToken cancellationToken);
}

public sealed class GroqService(IHttpClientFactory httpClientFactory) : IGroqService
{
    public async Task<string> CompleteAsync(IReadOnlyList<GroqMessage> messages, CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new GroqServiceException(
                StatusCodes.Status503ServiceUnavailable,
                "Assistant is not configured",
                "Set GROQ_API_KEY in the environment and restart the app.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model = Environment.GetEnvironmentVariable("GROQ_MODEL") ?? "openai/gpt-oss-20b",
            messages = messages.Select(message => new { role = message.Role, content = message.Content })
        });

        try
        {
            using var response = await httpClientFactory.CreateClient("Groq").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new GroqServiceException(
                    response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        ? StatusCodes.Status429TooManyRequests
                        : StatusCodes.Status502BadGateway,
                    response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        ? "AI service rate limit reached"
                        : "The AI service could not answer",
                    response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        ? "The AI service is busy right now. Please try again shortly."
                        : $"Groq returned HTTP {(int)response.StatusCode}. Check your API key, model, and available usage.");
            }

            var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
            var answer = payload?["choices"]?.AsArray()
                .FirstOrDefault()?["message"]?["content"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(answer))
            {
                throw new GroqServiceException(
                    StatusCodes.Status502BadGateway,
                    "No answer was returned",
                    "Try again or rephrase your request.");
            }

            return answer.Trim();
        }
        catch (HttpRequestException)
        {
            throw new GroqServiceException(
                StatusCodes.Status502BadGateway,
                "Could not reach the AI service",
                "Check your internet connection and try again.");
        }
        catch (JsonException)
        {
            throw new GroqServiceException(
                StatusCodes.Status502BadGateway,
                "The AI service returned an unreadable response",
                "Please try again in a moment.");
        }
        catch (InvalidOperationException)
        {
            throw new GroqServiceException(
                StatusCodes.Status502BadGateway,
                "The AI service returned an unreadable response",
                "Please try again in a moment.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GroqServiceException(
                StatusCodes.Status504GatewayTimeout,
                "The AI service took too long to respond",
                "Try again in a moment.");
        }
    }
}

public sealed record GroqMessage(string Role, string Content);

public sealed class GroqServiceException(int statusCode, string title, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
}
