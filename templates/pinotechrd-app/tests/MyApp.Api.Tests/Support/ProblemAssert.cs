using System.Text.Json;

namespace MyApp.Api.Tests.Support;

/// <summary>Assertions shared by every test that checks an error response of the API.</summary>
internal static class ProblemAssert
{
    /// <summary>
    /// Asserts the response is a problem document with the expected status and code, a content type of
    /// <c>application/problem+json</c> and a non-empty trace id, and returns the body.
    /// </summary>
    public static async Task<JsonElement> IsProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(text);
        var body = document.RootElement.Clone();
        Assert.Equal(status, body.GetProperty("status").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()), "traceId must not be empty.");
        return body;
    }
}
