using Irony.Parsing;

namespace FountainParserBenchmark;

public class IronyFountainGrammar : Grammar
{
    public IronyFountainGrammar() : base(caseSensitive: false)
    {
        // 1. Terminals
        var envInt = ToTerm("INT.");
        var envExt = ToTerm("EXT.");
        var envIntExt = ToTerm("INT./EXT.");
        var envIE = ToTerm("I/E.");
        var dotPrefix = ToTerm(".");

        var transitionTerm = new RegexBasedTerminal("transition", @"(>[\s\S]*?\n)|([A-Z\s]+TO:\s*\n)|(FADE IN[\.:]?\n)|(FADE OUT[\.:]?\n)");
        var centeredText = new RegexBasedTerminal("centered", @">\s*.+\s*<");
        var noteBlock = new RegexBasedTerminal("note", @"\[\[[\s\S]*?\]\]");
        var boneyardBlock = new RegexBasedTerminal("boneyard", @"/\*[\s\S]*?\*/");

        var freeLine = new FreeTextLiteral("freeLine", FreeTextOptions.None, "\r", "\n");

        // 2. Non-Terminals
        var envPrefix = new NonTerminal("envPrefix");
        var sceneHeading = new NonTerminal("sceneHeading");
        var element = new NonTerminal("element");
        var screenplay = new NonTerminal("screenplay");

        envPrefix.Rule = envInt | envExt | envIntExt | envIE;
        sceneHeading.Rule = (envPrefix + freeLine) | (dotPrefix + freeLine);
        element.Rule = sceneHeading | transitionTerm | centeredText | noteBlock | boneyardBlock | freeLine;

        screenplay.Rule = MakeStarRule(screenplay, element);

        this.Root = screenplay;
    }
}
