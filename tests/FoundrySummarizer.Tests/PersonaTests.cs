using FoundrySummarizer.Core.Personas;

namespace FoundrySummarizer.Tests;

public class PersonaTests
{
    [Fact]
    public void PromptyEngine_RegistersThreeBuiltInPersonas()
    {
        var engine = new PromptyEngine();
        Assert.Equal(3, engine.AvailablePersonas.Count);

        var exec = engine.GetPersona("Executive Bullets");
        var action = engine.GetPersona("Action-Item Extractor");
        var legal = engine.GetPersona("Legal Compliance Check");

        Assert.NotNull(exec);
        Assert.NotNull(action);
        Assert.NotNull(legal);

        Assert.Contains("Chief of Staff", exec.SystemPrompt);
        Assert.Contains("Action Items Breakdown", action.UserPromptTemplate);
        Assert.Contains("Liability Exposures", legal.UserPromptTemplate);
    }

    [Fact]
    public void PromptyEngine_RendersVariablesInChatMessages()
    {
        var engine = new PromptyEngine();
        var exec = engine.GetPersona("Executive Bullets")!;

        var vars = new Dictionary<string, string>
        {
            ["documentText"] = "Proposal to invest $150,000 in local hardware.",
            ["groundingContext"] = "NOTE: Exceeds $100k VP approval gate."
        };

        var messages = engine.RenderChatMessages(exec, vars);
        Assert.Equal(2, messages.Count);

        var userMsg = messages[1].Text!;
        Assert.Contains("Proposal to invest $150,000", userMsg);
        Assert.Contains("NOTE: Exceeds $100k", userMsg);
    }

    [Fact]
    public void PromptyEngine_ParsesCustomPrompty()
    {
        var custom = """
        ---
        name: Custom Technical Deep-Dive
        description: Highly detailed technical architecture review
        model:
          api: chat
          parameters:
            temperature: 0.3
            max_tokens: 2000
        ---
        system:
        You are a Principal Software Architect. Focus on latency, memory allocations, and concurrency.

        user:
        CODE REPOSITORY:
        {{documentText}}
        """;

        var engine = new PromptyEngine();
        var doc = engine.Parse(custom);

        Assert.Equal("Custom Technical Deep-Dive", doc.Name);
        Assert.Equal(0.3, doc.ModelConfig.Temperature);
        Assert.Equal(2000, doc.ModelConfig.MaxTokens);
        Assert.Contains("Principal Software Architect", doc.SystemPrompt);
        Assert.Contains("CODE REPOSITORY:", doc.UserPromptTemplate);
    }

    [Fact]
    public void PromptyDocument_ToChatOptions_CarriesPersonaSamplingSettings()
    {
        var engine = new PromptyEngine();
        var options = engine.GetPersona("Executive Bullets")!.ToChatOptions();

        Assert.Equal(0.1f, options.Temperature);
        Assert.Equal(0.9f, options.TopP);
        Assert.Equal(1500, options.MaxOutputTokens);
    }

    [Fact]
    public void PromptyEngine_ParsesDecimalsIndependentOfCurrentCulture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // de-DE uses ',' as the decimal separator, so a culture-sensitive parse reads "0.1" as 1.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var doc = new PromptyEngine().GetPersona("Legal Compliance Check")!;

            Assert.Equal(0.1, doc.ModelConfig.Temperature);
            Assert.Equal(0.9, doc.ModelConfig.TopP);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void PromptyDocument_DoesNotExpandPlaceholdersInsideSubstitutedValues()
    {
        var doc = new PromptyDocument { UserPromptTemplate = "<document>{{documentText}}</document>\n{{groundingContext}}" };
        var vars = new Dictionary<string, string>
        {
            ["documentText"] = "Literal {{groundingContext}} in source.",
            ["groundingContext"] = "POLICY"
        };

        var rendered = doc.RenderUserPrompt(vars);

        Assert.Equal("<document>Literal {{groundingContext}} in source.</document>\nPOLICY", rendered);
    }

    [Fact]
    public void PromptyEngine_IgnoresRoleWordsInsidePromptText()
    {
        var custom = """
        ---
        name: Role Word Test
        ---
        system:
        Explain things so that the end user: a non-expert, understands them.

        user:
        {{documentText}}
        """;

        var doc = new PromptyEngine().Parse(custom);

        Assert.Contains("the end user: a non-expert", doc.SystemPrompt);
        Assert.Equal("{{documentText}}", doc.UserPromptTemplate);
    }

    [Fact]
    public void BuiltInPersonas_WrapDocumentAndForbidInvention()
    {
        var engine = new PromptyEngine();
        foreach (var persona in engine.AvailablePersonas)
        {
            Assert.Contains("Use ONLY facts stated inside <document>", persona.SystemPrompt);
            Assert.Contains("<document>\n{{documentText}}\n</document>", persona.UserPromptTemplate);
        }
    }
}
