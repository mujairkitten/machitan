namespace Machitan.Core;

/// <summary>What this process was started to do.</summary>
internal enum StartupMode
{
    /// <summary>Run the shell (the normal case).</summary>
    Shell,

    /// <summary>Show only the Batch 0 backdrop proof window; Explorer is left alone.</summary>
    Proof,

    /// <summary>Show only the Batch 2 see-through test window; Explorer is left alone.</summary>
    SeeThrough,
}
