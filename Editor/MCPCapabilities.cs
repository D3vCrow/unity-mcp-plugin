namespace UnityMCP.Editor
{
    /// <summary>
    /// Capability + version signal for the server↔plugin handshake (drift resilience).
    ///
    /// The Node MCP server and this plugin ship on separate release trains. Before the
    /// handshake neither exchanged a version, so a call to a route an older peer lacked
    /// failed with no graceful fallback and no "why". The plugin now advertises
    /// <see cref="ProtocolVersion"/> and <see cref="PluginVersion"/> in its /api/ping
    /// response; the server gates negotiated features on the protocol version and degrades
    /// gracefully (warn once, fall back) when the peer is too old.
    ///
    /// Pattern ported (not copied) from Unity ML-Agents' UnityRLCapabilities.
    /// Keep <see cref="ProtocolVersion"/> in sync with the server's PROTOCOL_VERSION
    /// (src/capabilities.js). <see cref="PluginVersion"/> needs no syncing — it reads
    /// package.json at runtime.
    /// Ref: knowledge/research/2026-07-12-vet-unity-ml-agents.md
    /// </summary>
    public static class MCPCapabilities
    {
        /// <summary>
        /// Monotonic wire-protocol version. Bump when adding a capability the server may
        /// need to feature-detect (e.g. a new negotiated route). Current negotiated
        /// features by minimum protocol: batch-wire (component/batch-wire) → 1.
        /// </summary>
        public const int ProtocolVersion = 1;

        private static string _pluginVersion;

        /// <summary>
        /// Plugin package version, read from package.json at runtime.
        ///
        /// This used to be a hand-maintained literal and it drifted: the constant sat at
        /// 2.27.0 while package.json had reached 2.32.0, so every /api/ping advertised a
        /// five-release-old version. The server hit the identical bug and fixed it the same
        /// way — read the manifest instead of restating it (src/index.js, createRequire).
        /// A literal that has to be edited in lockstep with another file is drift waiting to
        /// happen, so there is no longer a literal to edit.
        ///
        /// Cached after first use: PackageInfo does a resolve and ping is a hot route. The
        /// lazy read is main-thread-safe because RouteRequest is dispatched to the main
        /// thread (see MCPBridgeServer.RouteRequest).
        ///
        /// Falls back to "0.0.0" when the assembly belongs to no package — i.e. the plugin
        /// was dropped into Assets/ instead of installed via Package Manager, which the
        /// README does not document. An honest "unknown" beats a fabricated version.
        /// </summary>
        public static string PluginVersion
        {
            get
            {
                if (string.IsNullOrEmpty(_pluginVersion))
                {
                    var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                        typeof(MCPCapabilities).Assembly);
                    _pluginVersion = packageInfo?.version ?? "0.0.0";
                }
                return _pluginVersion;
            }
        }
    }
}
