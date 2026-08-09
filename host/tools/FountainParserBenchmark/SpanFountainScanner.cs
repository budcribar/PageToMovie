using System;
using System.Collections.Generic;

namespace FountainParserBenchmark;

public enum SpanElementType
{
    TitlePageMeta,
    SceneHeading,
    Action,
    Character,
    Parenthetical,
    Dialogue,
    Transition,
    Centered,
    Note
}

public readonly ref struct SpanElement
{
    public SpanElementType Type { get; }
    public ReadOnlySpan<char> Text { get; }
    public ReadOnlySpan<char> Meta { get; }

    public SpanElement(SpanElementType type, ReadOnlySpan<char> text, ReadOnlySpan<char> meta = default)
    {
        Type = type;
        Text = text;
        Meta = meta;
    }
}

public static class SpanFountainScanner
{
    public static int Parse(ReadOnlySpan<char> text)
    {
        int elementCount = 0;

        bool prevBlank = true;
        bool inDialogue = false;

        int lineStart = 0;
        int len = text.Length;

        while (lineStart < len)
        {
            int lineEnd = text.Slice(lineStart).IndexOf('\n');
            int nextLineStart;

            ReadOnlySpan<char> line;
            if (lineEnd < 0)
            {
                line = text.Slice(lineStart);
                nextLineStart = len;
            }
            else
            {
                line = text.Slice(lineStart, lineEnd);
                nextLineStart = lineStart + lineEnd + 1;
            }

            line = line.Trim('\r').Trim();

            if (line.IsEmpty)
            {
                prevBlank = true;
                inDialogue = false;
                lineStart = nextLineStart;
                continue;
            }

            // 1. Scene Heading
            if (IsSceneHeading(line))
            {
                inDialogue = false;
                elementCount++;
            }
            // 2. Centered Text
            else if (line.Length >= 2 && line[0] == '>' && line[line.Length - 1] == '<')
            {
                inDialogue = false;
                elementCount++;
            }
            // 3. Forced Transition or Transition ending in TO: / OUT.
            else if (IsTransition(line))
            {
                inDialogue = false;
                elementCount++;
            }
            // 4. Note [[...]]
            else if (line.Length >= 4 && line.StartsWith("[[") && line.EndsWith("]]"))
            {
                elementCount++;
            }
            // 5. Parenthetical inside dialogue
            else if (inDialogue && line.Length >= 2 && line[0] == '(' && line[line.Length - 1] == ')')
            {
                elementCount++;
            }
            // 6. Character Cue (uppercase line preceded by blank line)
            else if (prevBlank && IsCharacterCue(line))
            {
                inDialogue = true;
                elementCount++;
            }
            // 7. Dialogue or Action
            else
            {
                elementCount++;
            }

            prevBlank = false;
            lineStart = nextLineStart;
        }

        return elementCount;
    }

    public static bool IsSceneHeading(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty) return false;

        if (line[0] == '.' && (line.Length == 1 || line[1] != '.'))
            return true;

        return StartsWithEnv(line, "INT.") ||
               StartsWithEnv(line, "EXT.") ||
               StartsWithEnv(line, "INT./EXT.") ||
               StartsWithEnv(line, "INT/EXT.") ||
               StartsWithEnv(line, "I/E.") ||
               StartsWithEnv(line, "EST.");
    }

    private static bool StartsWithEnv(ReadOnlySpan<char> line, string env)
    {
        if (line.StartsWith(env, StringComparison.OrdinalIgnoreCase))
        {
            if (line.Length == env.Length) return true;
            char next = line[env.Length];
            return next == ' ' || next == '.' || next == '-';
        }
        return false;
    }

    public static bool IsTransition(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty) return false;
        if (line[0] == '>' && (line.Length == 1 || line[line.Length - 1] != '<')) return true;

        if (line.EndsWith("TO:", StringComparison.OrdinalIgnoreCase) ||
            line.EndsWith("OUT.", StringComparison.OrdinalIgnoreCase) ||
            line.EndsWith("BLACK.", StringComparison.OrdinalIgnoreCase))
        {
            return IsUpperWithoutPunctuation(line);
        }

        return false;
    }

    public static bool IsCharacterCue(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty) return false;
        if (line[0] == '@') return true;

        int parenIdx = line.IndexOf('(');
        ReadOnlySpan<char> speaker = parenIdx >= 0 ? line.Slice(0, parenIdx).Trim() : line;

        if (speaker.IsEmpty) return false;

        return IsUpperWithoutPunctuation(speaker);
    }

    private static bool IsUpperWithoutPunctuation(ReadOnlySpan<char> span)
    {
        bool hasLetter = false;
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (char.IsLower(c)) return false;
            if (char.IsLetter(c)) hasLetter = true;
        }
        return hasLetter;
    }
}
