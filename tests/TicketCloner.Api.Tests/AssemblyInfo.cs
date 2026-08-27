// Serilog's request logging writes through the STATIC Log.Logger, which every
// hosted app overwrites at startup. Run collections in parallel and two hosts
// race for it: log lines surface under the wrong test, or a sink is disposed
// out from under a host that is still serving.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
