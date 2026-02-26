using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Repl.ShellCompletion;

internal sealed partial class ShellCompletionRuntime
{
	private sealed class ShellCompletionStatusModel
	{
		[Display(Order = 1)]
		public bool Enabled { get; init; }

		[Display(Name = "Setup mode", Order = 2)]
		public string SetupMode { get; init; } = string.Empty;

		[Display(Name = "Detected shell", Order = 3)]
		public string Detected { get; init; } = string.Empty;

		[Display(Name = "Bash profile", Order = 4)]
		public string BashProfilePath { get; init; } = string.Empty;

		[Display(Name = "Bash installed", Order = 5)]
		public bool BashInstalled { get; init; }

		[Display(Name = "PowerShell profile", Order = 6)]
		public string PowerShellProfilePath { get; init; } = string.Empty;

		[Display(Name = "PowerShell installed", Order = 7)]
		public bool PowerShellInstalled { get; init; }

		[Display(Name = "Zsh profile", Order = 8)]
		public string ZshProfilePath { get; init; } = string.Empty;

		[Display(Name = "Zsh installed", Order = 9)]
		public bool ZshInstalled { get; init; }

		[Browsable(false)]
		public string DetectedShell { get; init; } = string.Empty;

		[Browsable(false)]
		public string DetectionReason { get; init; } = string.Empty;
	}

	private sealed class ShellCompletionDetectShellModel
	{
		[Display(Name = "Detected shell", Order = 1)]
		public string Detected { get; init; } = string.Empty;

		[Browsable(false)]
		public string DetectedShell { get; init; } = string.Empty;

		[Browsable(false)]
		public string DetectionReason { get; init; } = string.Empty;
	}

	private sealed class ShellCompletionInstallModel
	{
		[Display(Order = 1)]
		public bool Success { get; init; }

		[Display(Order = 2)]
		public bool Changed { get; init; }

		[Display(Order = 3)]
		public string Shell { get; init; } = string.Empty;

		[Display(Name = "Profile path", Order = 4)]
		public string ProfilePath { get; init; } = string.Empty;

		[Display(Order = 5)]
		public string Message { get; init; } = string.Empty;
	}

	private sealed class ShellCompletionUninstallModel
	{
		[Display(Order = 1)]
		public bool Success { get; init; }

		[Display(Order = 2)]
		public bool Changed { get; init; }

		[Display(Order = 3)]
		public string Shell { get; init; } = string.Empty;

		[Display(Name = "Profile path", Order = 4)]
		public string ProfilePath { get; init; } = string.Empty;

		[Display(Order = 5)]
		public string Message { get; init; } = string.Empty;
	}

	private readonly record struct ShellCompletionOperationResult(
		bool Success,
		bool Changed,
		string ProfilePath,
		string Message);

	private readonly record struct ShellDetectionResult(
		ShellKind Kind,
		string Reason,
		bool ParentLooksLikeWindowsPowerShell = false);

	private readonly record struct ShellSignal(
		int PowershellScore,
		int BashScore,
		int ZshScore,
		bool HasKnownUnsupported,
		string Reason,
		bool ParentLooksLikeWindowsPowerShell);

	private sealed class ShellCompletionState
	{
		public bool PromptShown { get; set; }

		public string? LastDetectedShell { get; set; }

		public List<string> InstalledShells { get; set; } = [];
	}
}
