using System.CommandLine;
using ModelingEvolution.Mjpeg.Cli.Actions;
using ModelingEvolution.Mjpeg.Cli.Commands;

var rootCommand = new RootCommand("MJPEG CLI - Extract and process MJPEG recordings");

rootCommand.Subcommands.Add(ExtractCommand.Create(ExtractAction.Execute));
rootCommand.Subcommands.Add(ConvertCommand.Create(ConvertAction.Execute));

return rootCommand.Parse(args).Invoke();
