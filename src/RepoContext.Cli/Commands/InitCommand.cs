using System.CommandLine;
using RepoContext.Core;
using RepoContext.Core.Configuration;

namespace RepoContext.Cli.Commands;

/// <summary>The <c>repoctx init</c> command (spec F1).</summary>
public static class InitCommand
{
    public static Command Build()
    {
        var force = new Option<bool>("--force")
        {
            Description = "Overwrite an existing configuration.",
        };

        var command = new Command("init", "Initialize a RepoContext index in the current repository.")
        {
            force,
        };

        command.SetAction(parseResult =>
        {
            RepoLayout layout = RepoLayout.For(Directory.GetCurrentDirectory());
            try
            {
                InitResult result = Initializer.Initialize(layout, parseResult.GetValue(force));
                string what = result.ConfigOverwritten ? "Reinitialized" : "Initialized";
                Console.WriteLine($"{what} RepoContext in {layout.Root}");
                Console.WriteLine($"  wrote {RepoContextInfo.ConfigFileName}");
                if (result.GitignoreUpdated)
                {
                    Console.WriteLine("  updated .gitignore (.repoctx/)");
                }

                Console.WriteLine("Next: run 'repoctx index'.");
                return ExitCode.Success;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCode.Error;
            }
        });

        return command;
    }
}
