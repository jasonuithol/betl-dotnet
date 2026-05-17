// Lets the dotnet.pipelinecomponent driver construct internal shim
// implementations (BetlInputColumn, BetlBufferManager, BetlSyncBuffer's
// DrainOutput, etc.) without making them public.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Betl.Providers.Dotnet")]
