using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace DocsMcpSample.Search;

internal sealed class E5Embedding : ITextEmbedding, IDisposable
{
	public int MaxTokens => 512;
	private readonly SentencePieceTokenizer _tokenizer;
	private readonly InferenceSession _model;
	private readonly Lock _tokenizerGate = new();
	public string Identity => "multilingual-e5-small:614241f622f53c4eeff9890bdc4f31cfecc418b3:mean-l2:xlmr-v1";
	public int Dimensions => 384;

	public E5Embedding(Settings settings)
	{
		OrtEnv.Instance().DisableTelemetryEvents();
		using var vocabulary = File.OpenRead(Path.Combine(settings.Assets, "sentencepiece.bpe.model"));
		_tokenizer = SentencePieceTokenizer.Create(vocabulary, addBeginningOfSentence: false, addEndOfSentence: false);
		using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount)), InterOpNumThreads = 1 };
		_model = new InferenceSession(Path.Combine(settings.Assets, "model.onnx"), options);
	}

	public int CountPassageTokens(string text)
	{
		lock (_tokenizerGate) { return _tokenizer.CountTokens("passage: " + text, addBeginningOfSentence: false, addEndOfSentence: false) + 2; }
	}

	internal long[] Encode(string text)
	{
		lock (_tokenizerGate)
		{
			var pieces = _tokenizer.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);
			if (pieces.Count + 2 > MaxTokens) { throw new ArgumentException("Text exceeds the model's 512-token window."); }
			// XLM-R reserves 0..3 in fairseq order; raw SentencePiece IDs must be shifted.
			return [0, .. pieces.Select(id => (long)(id == 0 ? 3 : id + 1)), 2];
		}
	}

	public float[] Embed(string text, bool query, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var ids = Encode((query ? "query: " : "passage: ") + text);
		long[] shape = [1, ids.Length];
		using var input = OrtValue.CreateTensorValueFromMemory(ids, shape);
		using var mask = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, ids.Length).ToArray(), shape);
		using var types = OrtValue.CreateTensorValueFromMemory(new long[ids.Length], shape);
		var inputs = new Dictionary<string, OrtValue>(StringComparer.Ordinal) { ["input_ids"] = input, ["attention_mask"] = mask };
		if (_model.InputMetadata.ContainsKey("token_type_ids")) { inputs["token_type_ids"] = types; }
		using var run = new RunOptions();
		using var cancellation = cancellationToken.Register(() => run.Terminate = true);
		try
		{
			using var output = _model.Run(run, inputs, ["last_hidden_state"]);
			var states = output[0].GetTensorDataAsSpan<float>();
			var vector = new float[Dimensions];
			for (var token = 0; token < ids.Length; token++)
			{
				for (var dimension = 0; dimension < Dimensions; dimension++) { vector[dimension] += states[token * Dimensions + dimension] / ids.Length; }
			}
			var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
			if (norm == 0 || !double.IsFinite(norm)) { throw new InvalidOperationException("The embedding model returned an invalid vector."); }
			for (var dimension = 0; dimension < Dimensions; dimension++) { vector[dimension] /= (float)norm; }
			return vector;
		}
		catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
	}

	public void Dispose() => _model.Dispose();
}
