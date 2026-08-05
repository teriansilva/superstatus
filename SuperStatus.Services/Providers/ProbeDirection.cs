namespace SuperStatus.Services.Providers
{
    /// <summary>
    /// #335: which way a provider's probe flows. <see cref="Pull"/> providers reach
    /// out to the target each tick (http, ai); <see cref="Push"/> providers wait for
    /// the target to ping in (heartbeat). Surfaced on the descriptor so UI surfaces
    /// (the Plugins page) can label the inverted direction without page-local
    /// provider knowledge.
    /// </summary>
    public enum ProbeDirection
    {
        Pull = 0,
        Push = 1,
    }
}
