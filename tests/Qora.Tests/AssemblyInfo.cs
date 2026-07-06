// The Qora front end is invoked one parse at a time in production (a CLI process per compile, one extension
// spawn per keystroke), and the Janglim engine under QoraParser.Parse keeps process-global parser state, so
// it is NOT safe to run parses concurrently. xUnit parallelizes test CLASSES by default; disable that so the
// suite matches how the compiler is actually used (and stays deterministic).
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
