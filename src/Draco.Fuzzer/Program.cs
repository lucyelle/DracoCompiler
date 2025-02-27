using System.CommandLine;
using Draco.Compiler.Internal.Syntax;
using Draco.Fuzzer.Components;
using Draco.Fuzzer.Generators;

namespace Draco.Fuzzer;

internal static class Program
{
    internal static int Main(string[] args) => ConfigureCommands().Invoke(args);

    private static RootCommand ConfigureCommands()
    {
        var numEpochsOption = new Option<int>(new string[] { "-e", "--epochs" }, () => -1, description: "Specifies the number of epochs the fuzzer should run for, if not specified or -1, the fuzzer will run indefinitely");
        var numMutationsOption = new Option<int>(new string[] { "-m", "--mutations" }, () => 0, description: "Specifies the number of mutations the fuzzer should make for each epoch, if not specified or 0, there will be no mutations");

        var lexerCommand = new Command("lexer", "Fuzzes the lexer")
        {
            numEpochsOption,
            numMutationsOption,
        };
        lexerCommand.SetHandler(FuzzLexer, numEpochsOption, numMutationsOption);

        var parserCommand = new Command("parser", "Fuzzes the parser")
        {
            numEpochsOption,
            numMutationsOption,
        };
        parserCommand.SetHandler(FuzzParser, numEpochsOption, numMutationsOption);

        var e2eCommand = new Command("e2e", "Fuzzes the compiler end-to-end")
        {
            numEpochsOption,
            numMutationsOption,
        };
        e2eCommand.SetHandler(FuzzE2e, numEpochsOption, numMutationsOption);

        var rootCommand = new RootCommand("CLI for the Draco fuzzer");
        rootCommand.AddCommand(lexerCommand);
        rootCommand.AddCommand(parserCommand);
        rootCommand.AddCommand(e2eCommand);
        return rootCommand;
    }

    private static void FuzzLexer(int numEpochs, int numMutations) =>
        Fuzz(numEpochs, numMutations, new LexerFuzzer(Generator.String()));

    private static void FuzzParser(int numEpochs, int numMutations) =>
        Fuzz(numEpochs, numMutations, new ParserFuzzer(
            new TokenGenerator().Sequence().Append(SyntaxToken.From(Compiler.Api.Syntax.TokenKind.EndOfInput))));

    private static void FuzzE2e(int numEpochs, int numMutations) =>
        Fuzz(numEpochs, numMutations, new E2eFuzzer(Generator.String()));

    private static void Fuzz(int numEpochs, int numMutations, IComponentFuzzer componentFuzzer)
    {
        const int nEpochFeedback = 10;

        try
        {
            for (var i = 0; (i < numEpochs || numEpochs == -1); i++)
            {
                if (i % nEpochFeedback == 0) Console.Error.WriteLine($"Epoch {i}...");

                componentFuzzer.NextEpoch();
                for (var j = 0; j < numMutations; j++) componentFuzzer.NextMutation();
            }
        }
        catch (CrashException ex)
        {
            Console.Error.WriteLine("Fuzzer crashed!");
            Console.Error.WriteLine("Input:");
            Console.Error.WriteLine("==========");
            Console.Error.WriteLine(ex.Input);
            Console.Error.WriteLine("==========");
            Console.Error.WriteLine($"Original exception: {ex.OriginalException.Message}");
            Console.Error.WriteLine("Trace:");
            Console.Error.WriteLine(ex.OriginalException.StackTrace);
            Environment.Exit(1);
        }
        catch (MutationException ex)
        {
            Console.Error.WriteLine("Fuzzer crashed on incremental change!");
            Console.Error.WriteLine("Previous Input:");
            Console.Error.WriteLine("==========");
            Console.Error.WriteLine(ex.OldInput);
            Console.Error.WriteLine("==========");
            Console.Error.WriteLine("New Input:");
            Console.Error.WriteLine("==========");
            Console.Error.WriteLine(ex.NewInput);
            Console.Error.WriteLine("==========");
            Console.Error.WriteLine($"Original exception: {ex.OriginalException.Message}");
            Console.Error.WriteLine("Trace:");
            Console.Error.WriteLine(ex.OriginalException.StackTrace);
            Environment.Exit(2);
        }
    }
}
