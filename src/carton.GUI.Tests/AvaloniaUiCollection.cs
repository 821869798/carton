using Xunit;

namespace carton.GUI.Tests;

// Avalonia initialization owns process-global URI parsers and render services.
[CollectionDefinition("Avalonia UI", DisableParallelization = true)]
public sealed class AvaloniaUiCollection;
