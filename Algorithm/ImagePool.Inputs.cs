using OpenCvSharp;

namespace AiVisionStudio.Algorithm;

public partial class ImagePool
{
    private readonly Dictionary<int, (Mat Image, string Owner)> _inputImages = new();
    internal string InputOwner(int slot) => _inputImages.TryGetValue(slot, out var input) ? input.Owner : null;
    internal IDisposable UseInputs(IReadOnlyList<(int Slot, Mat Image, string Owner)> inputs) => new InputScope(this, inputs);

    private sealed class InputScope : IDisposable
    {
        private readonly ImagePool _pool;
        private readonly IReadOnlyList<(int Slot, Mat Image, string Owner)> _inputs;
        private readonly Dictionary<int, (Mat Image, string Owner)> _previous;
        public InputScope(ImagePool pool, IReadOnlyList<(int Slot, Mat Image, string Owner)> inputs)
        {
            _pool = pool; _inputs = inputs; _previous = new(pool._inputImages);
            foreach (var input in inputs) pool._inputImages[input.Slot] = (input.Image, input.Owner);
        }
        public void Dispose()
        {
            _pool._inputImages.Clear();
            foreach (var input in _previous) _pool._inputImages[input.Key] = input.Value;
            foreach (var input in _inputs)
                if (!_pool._images.Values.Any(image => ReferenceEquals(image, input.Image))) input.Image.Dispose();
        }
    }
}
