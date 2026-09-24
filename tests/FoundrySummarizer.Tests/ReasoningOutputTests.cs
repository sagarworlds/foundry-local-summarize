using System.Net;
using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Tests;

public class ReasoningOutputTests
{
    [Theory]
    [InlineData("<think>The budget is maybe $1m?</think>\n\n### Summary\n- Budget: $150,000", "### Summary\n- Budget: $150,000", true)]
    [InlineData("<THINK>\nhmm\n</THINK>Answer", "Answer", true)]
    [InlineData("<think>\n\n</think>\n\nAnswer", "Answer", true)]                               // Qwen3 with /no_think
    [InlineData("the user wants a summary...</think>\nAnswer", "Answer", true)]                 // opening tag was in the template
    [InlineData("Answer<think>second thoughts cut off by the output limit", "Answer", true)]
    [InlineData("<think>only thinking, cut off", "", true)]
    [InlineData("Plain answer without reasoning.", "Plain answer without reasoning.", false)]
    [InlineData("", "", false)]
    public void RemoveReasoning_KeepsOnlyTheAnswer(string raw, string expected, bool expectedHadReasoning)
    {
        Assert.Equal(expected, ReasoningOutputFilter.RemoveReasoning(raw, out var hadReasoning));
        Assert.Equal(expectedHadReasoning, hadReasoning);
    }

    [Fact]
    public void SuppressThinking_AddsTheDirectiveOnlyForQwen3AndOnlyOnce()
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, "sys"), new(ChatRole.User, "Summarize.") };

        var qwen3 = ReasoningOutputFilter.SuppressThinking("qwen3-0.6b-generic-gpu:1", messages);
        Assert.Equal("Summarize.\n\n/no_think", qwen3[^1].Text);
        Assert.Equal("Summarize.", messages[^1].Text);                                            // input untouched
        Assert.Same(qwen3, ReasoningOutputFilter.SuppressThinking("qwen3-0.6b-generic-gpu:1", qwen3)); // not added twice

        Assert.Same(messages, ReasoningOutputFilter.SuppressThinking("Phi-4-mini-instruct-generic-gpu:5", messages));
    }

    private static (FoundryLocalChatClient Client, List<string> Bodies) ClientAnswering(string modelId, string answer)
    {
        var bodies = new List<string>();
        var completion = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "c1", @object = "chat.completion", created = 1, model = modelId,
            choices = new[] { new { index = 0, message = new { role = "assistant", content = answer }, finish_reason = "stop" } }
        });
        var server = new BodyRecordingServer(path => path switch
        {
            "/openai/loadedmodels" => $"[\"{modelId}\"]",
            "/v1/chat/completions" => completion,
            _ => "{}"
        }, bodies);

        var options = new FoundryOptions();
        options.Local.ModelId = modelId;
        options.Local.AutoSelectModel = false;
        var cli = new StatusCli("🟢 Model management service is running on http://127.0.0.1:5273/openai/status");
        return (new FoundryLocalChatClient(options, new FoundryLocalService(options, cli, server)), bodies);
    }

    [Fact]
    public async Task ChatClient_ReturnsOnlyTheAnswer_AndAsksQwen3NotToThink()
    {
        var (client, bodies) = ClientAnswering("qwen3-0.6b-generic-gpu:1", "<think>\nLet me think.\n</think>\n\nThe budget is $150,000.");
        using (client)
        {
            var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "What is the budget?") });

            Assert.Equal("The budget is $150,000.", response.Text);
            Assert.Contains("/no_think", Assert.Single(bodies));
        }
    }

    [Fact]
    public async Task ChatClient_ExplainsAModelThatOnlyThinks()
    {
        var (client, _) = ClientAnswering("deepseek-r1-7b-generic-gpu:1", "<think>Still thinking when the output limit hit");
        using (client)
        {
            var ex = await Assert.ThrowsAsync<LocalModelUnavailableException>(() =>
                client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Summarize.") }));

            Assert.Contains("spent its whole answer on reasoning", ex.Message);
        }
    }

    private sealed class StatusCli(string output) : IFoundryCli
    {
        public Task<FoundryCliResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FoundryCliResult(true, output, null));
    }

    private sealed class BodyRecordingServer(Func<string, string> respond, List<string> chatBodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/chat/completions" && request.Content is not null)
            {
                chatBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(path)) };
        }
    }
}
