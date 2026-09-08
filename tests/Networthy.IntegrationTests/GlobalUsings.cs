global using Xunit;

// The AG-UI stream parser and the golden-eval case record now ship in the Plenipo.Testing kit
// (they were copies of the platform's own, and a protocol change had to be hand-carried into every
// product). The aliases keep this suite reading as it did.
global using EvalRun = Plenipo.Testing.AgUi.AgUiRun;
global using EvalCase = Plenipo.Testing.Evals.EvalCase;
