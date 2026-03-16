using System.Collections.ObjectModel;
using Repl.Interaction;

namespace Repl.TerminalGui;

/// <summary>
/// Handles REPL interaction requests using Terminal.Gui modal dialogs.
/// Each prompt type is rendered as a native TUI dialog overlay.
/// </summary>
public sealed class TerminalGuiInteractionHandler : IReplInteractionHandler
{
	/// <inheritdoc />
	public ValueTask<InteractionResult> TryHandleAsync(
		InteractionRequest request, CancellationToken cancellationToken) => request switch
	{
		AskTextRequest r => HandleTextAsync(r),
		AskChoiceRequest r => HandleChoiceAsync(r),
		AskMultiChoiceRequest r => HandleMultiChoiceAsync(r),
		AskConfirmationRequest r => HandleConfirmationAsync(r),
		AskSecretRequest r => HandleSecretAsync(r),
		_ => new ValueTask<InteractionResult>(InteractionResult.Unhandled),
	};

#pragma warning disable CS0618 // Static Application API — Terminal.Gui v2 develop

	private static ValueTask<InteractionResult> HandleTextAsync(AskTextRequest request)
	{
		var tcs = new TaskCompletionSource<InteractionResult>();

		Application.Invoke(() =>
		{
			var textField = new TextField
			{
				X = 1, Y = 1, Width = Dim.Fill(1),
				Text = request.DefaultValue ?? string.Empty,
			};

			var dialog = CreateDialog(request.Prompt, widthPercent: 60, height: 5);
			dialog.Add(textField);

			AddDialogButtons(dialog, tcs,
				onOk: () => InteractionResult.Success(textField.Text ?? string.Empty),
				onCancel: () => InteractionResult.Success(request.DefaultValue ?? string.Empty));

			textField.SetFocus();
			Application.Run(dialog);
			tcs.TrySetResult(InteractionResult.Success(textField.Text ?? string.Empty));
		});

		return new ValueTask<InteractionResult>(tcs.Task);
	}

	private static ValueTask<InteractionResult> HandleChoiceAsync(AskChoiceRequest request)
	{
		var tcs = new TaskCompletionSource<InteractionResult>();

		Application.Invoke(() =>
		{
			var displayChoices = new ObservableCollection<string>(
				request.Choices.Select(StripMnemonics));

			var listView = new ListView
			{
				X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(2),
			};
			listView.SetSource(displayChoices);
			listView.SelectedItem = request.DefaultIndex;

			var dialog = CreateDialog(request.Prompt, widthPercent: 60, height: Math.Min(request.Choices.Count + 6, 20));
			dialog.Add(listView);

			AddDialogButtons(dialog, tcs,
				onOk: () => InteractionResult.Success(listView.SelectedItem ?? request.DefaultIndex),
				onCancel: () => InteractionResult.Success(request.DefaultIndex));

			listView.Accepting += (_, e) =>
			{
				tcs.TrySetResult(InteractionResult.Success(listView.SelectedItem ?? request.DefaultIndex));
				Application.RequestStop(dialog);
				e.Handled = true;
			};

			listView.SetFocus();
			Application.Run(dialog);
			tcs.TrySetResult(InteractionResult.Success(request.DefaultIndex));
		});

		return new ValueTask<InteractionResult>(tcs.Task);
	}

	private static ValueTask<InteractionResult> HandleMultiChoiceAsync(AskMultiChoiceRequest request)
	{
		var tcs = new TaskCompletionSource<InteractionResult>();

		Application.Invoke(() =>
		{
			var displayChoices = request.Choices.Select(StripMnemonics).ToArray();
			var selected = new HashSet<int>(request.DefaultIndices ?? []);
			var dialog = CreateDialog(request.Prompt, widthPercent: 60, height: Math.Min(request.Choices.Count + 6, 20));

			var checkBoxes = new List<CheckBox>();

			for (var i = 0; i < displayChoices.Length; i++)
			{
				var cb = new CheckBox
				{
					X = 1, Y = 1 + i,
					Text = displayChoices[i],
					Value = selected.Contains(i) ? CheckState.Checked : CheckState.UnChecked,
				};
				checkBoxes.Add(cb);
				dialog.Add(cb);
			}

			AddDialogButtons(dialog, tcs,
				onOk: () => InteractionResult.Success(GetCheckedIndices(checkBoxes)),
				onCancel: () => InteractionResult.Success((IReadOnlyList<int>)(request.DefaultIndices ?? [])));

			if (checkBoxes.Count > 0)
			{
				checkBoxes[0].SetFocus();
			}

			Application.Run(dialog);
			tcs.TrySetResult(InteractionResult.Success(GetCheckedIndices(checkBoxes)));
		});

		return new ValueTask<InteractionResult>(tcs.Task);
	}

	private static ValueTask<InteractionResult> HandleConfirmationAsync(AskConfirmationRequest request)
	{
		var tcs = new TaskCompletionSource<InteractionResult>();

		Application.Invoke(() =>
		{
			var dialog = CreateDialog(request.Prompt, widthPercent: 50, height: 5);

			var label = new Label
			{
				X = 1, Y = 1,
				Text = request.DefaultValue ? "Default: Yes" : "Default: No",
			};
			dialog.Add(label);

			var yesButton = new Button { Text = "Yes", IsDefault = request.DefaultValue };
			yesButton.Accepting += (_, e) =>
			{
				tcs.TrySetResult(InteractionResult.Success(value: true));
				Application.RequestStop(dialog);
				e.Handled = true;
			};

			var noButton = new Button { Text = "No", IsDefault = !request.DefaultValue };
			noButton.Accepting += (_, e) =>
			{
				tcs.TrySetResult(InteractionResult.Success(value: false));
				Application.RequestStop(dialog);
				e.Handled = true;
			};

			dialog.AddButton(yesButton);
			dialog.AddButton(noButton);

			Application.Run(dialog);
			tcs.TrySetResult(InteractionResult.Success(request.DefaultValue));
		});

		return new ValueTask<InteractionResult>(tcs.Task);
	}

	private static ValueTask<InteractionResult> HandleSecretAsync(AskSecretRequest request)
	{
		var tcs = new TaskCompletionSource<InteractionResult>();

		Application.Invoke(() =>
		{
			var textField = new TextField
			{
				X = 1, Y = 1, Width = Dim.Fill(1),
				Secret = true,
			};

			var dialog = CreateDialog(request.Prompt, widthPercent: 60, height: 5);
			dialog.Add(textField);

			AddDialogButtons(dialog, tcs,
				onOk: () => InteractionResult.Success(textField.Text ?? string.Empty),
				onCancel: () => InteractionResult.Success(string.Empty));

			textField.SetFocus();
			Application.Run(dialog);
			tcs.TrySetResult(InteractionResult.Success(textField.Text ?? string.Empty));
		});

		return new ValueTask<InteractionResult>(tcs.Task);
	}

	private static Dialog CreateDialog(string title, int widthPercent, int height) => new()
	{
		Title = title,
		Width = Dim.Percent(widthPercent),
		Height = height,
	};

	private static void AddDialogButtons(
		Dialog dialog,
		TaskCompletionSource<InteractionResult> tcs,
		Func<InteractionResult> onOk,
		Func<InteractionResult> onCancel)
	{
		var okButton = new Button { Text = "OK", IsDefault = true };
		okButton.Accepting += (_, e) =>
		{
			tcs.TrySetResult(onOk());
			Application.RequestStop(dialog);
			e.Handled = true;
		};

		var cancelButton = new Button { Text = "Cancel" };
		cancelButton.Accepting += (_, e) =>
		{
			tcs.TrySetResult(onCancel());
			Application.RequestStop(dialog);
			e.Handled = true;
		};

		dialog.AddButton(okButton);
		dialog.AddButton(cancelButton);
	}

#pragma warning restore CS0618

	private static int[] GetCheckedIndices(List<CheckBox> checkBoxes) =>
		checkBoxes
			.Select((cb, idx) => (cb, idx))
			.Where(x => x.cb.Value == CheckState.Checked)
			.Select(x => x.idx)
			.ToArray();

	/// <summary>
	/// Strips mnemonic markers (underscore convention) from a label.
	/// E.g. "_Abort" → "Abort", "No_thing" → "Nothing", "__real" → "_real".
	/// </summary>
	private static string StripMnemonics(string label)
	{
		var result = new char[label.Length];
		var writeIndex = 0;

		for (var i = 0; i < label.Length; i++)
		{
			if (label[i] == '_' && i + 1 < label.Length)
			{
				if (label[i + 1] == '_')
				{
					// Escaped underscore — emit one underscore.
					result[writeIndex++] = '_';
					i++;
				}
				else
				{
					// Mnemonic marker — skip the underscore, keep the next char.
				}
			}
			else
			{
				result[writeIndex++] = label[i];
			}
		}

		return new string(result, 0, writeIndex);
	}
}
