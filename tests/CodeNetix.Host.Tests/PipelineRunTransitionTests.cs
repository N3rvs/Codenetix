using CodeNetix.Contracts;
using CodeNetix.Pipeline.Domain;

namespace CodeNetix.Host.Tests;

/// <summary>Tabla exhaustiva de AD-2: 8 Estados × 8 métodos de <see cref="PipelineRun"/> (historia 1.6).</summary>
public sealed class PipelineRunTransitionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, Func<PipelineRun, StateChanged>> Methods = new()
    {
        ["StartAnalysis"] = r => r.StartAnalysis(),
        ["StartCoding"] = r => r.StartCoding(),
        ["StartReview"] = r => r.StartReview(),
        ["StartReviewRound"] = r => r.StartReviewRound(),
        ["StartDeciding"] = r => r.StartDeciding(),
        ["CompletePullRequest"] = r => r.CompletePullRequest(),
        ["Fail"] = r => r.Fail(FailureReason.StageError, "detalle"),
        ["Cancel"] = r => r.Cancel(),
    };

    /// <summary>(desde, método) → Estado resultante; lo que no aparece es ilegal.</summary>
    private static readonly Dictionary<(RunState From, string Method), RunState> Legal = new()
    {
        [(RunState.Received, "StartAnalysis")] = RunState.Analyzing,
        [(RunState.Analyzing, "StartCoding")] = RunState.Coding,
        [(RunState.Coding, "StartReview")] = RunState.Reviewing,
        [(RunState.Reviewing, "StartReviewRound")] = RunState.Coding,
        [(RunState.Reviewing, "StartDeciding")] = RunState.Deciding,
        [(RunState.Deciding, "CompletePullRequest")] = RunState.PullRequestCreated,

        [(RunState.Received, "Fail")] = RunState.Failed,
        [(RunState.Analyzing, "Fail")] = RunState.Failed,
        [(RunState.Coding, "Fail")] = RunState.Failed,
        [(RunState.Reviewing, "Fail")] = RunState.Failed,
        [(RunState.Deciding, "Fail")] = RunState.Failed,

        [(RunState.Received, "Cancel")] = RunState.Cancelled,
        [(RunState.Analyzing, "Cancel")] = RunState.Cancelled,
        [(RunState.Coding, "Cancel")] = RunState.Cancelled,
        [(RunState.Reviewing, "Cancel")] = RunState.Cancelled,
    };

    public static TheoryData<RunState, string> AllCombinations()
    {
        var data = new TheoryData<RunState, string>();
        foreach (var state in Enum.GetValues<RunState>())
        {
            foreach (var method in Methods.Keys)
            {
                data.Add(state, method);
            }
        }

        return data;
    }

    [Fact]
    public void The_table_covers_8_states_by_8_methods()
    {
        Assert.Equal(8, Enum.GetValues<RunState>().Length);
        Assert.Equal(8, Methods.Count);
        Assert.Equal(64, AllCombinations().Count);
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void Every_transition_follows_AD2(RunState from, string method)
    {
        var run = RunIn(from);

        if (Legal.TryGetValue((from, method), out var expected))
        {
            var change = Methods[method](run);

            Assert.Equal(expected, run.State);
            Assert.Equal(from, change.From);
            Assert.Equal(expected, change.To);
            if (expected == RunState.Failed)
            {
                Assert.Equal(FailureReason.StageError, change.FailureReason);
                Assert.Equal("detalle", change.Detail);
                Assert.Equal(FailureReason.StageError, run.FailureReason);
                Assert.Equal("detalle", run.FailureDetail);
            }
            else
            {
                Assert.Null(change.FailureReason);
                Assert.Null(change.Detail);
            }
        }
        else
        {
            var ex = Assert.Throws<InvalidRunTransitionException>(() => Methods[method](run));
            Assert.Equal(from, ex.State);
            Assert.Equal(method, ex.Method);
            Assert.Equal(from, run.State); // el Estado no cambia
        }
    }

    [Fact]
    public void Create_starts_in_Received_with_RunCreated_and_initial_StateChanged()
    {
        var eventId = Guid.CreateVersion7();

        var (run, created, initial) = PipelineRun.Create(eventId, "github:o/r#1", T0);

        Assert.Equal(RunState.Received, run.State);
        Assert.Equal(7, run.Id.Version);
        Assert.Equal(eventId, run.EventId);
        Assert.Equal("github:o/r#1", run.ObjectKey);
        Assert.Equal(T0, run.CreatedAt);
        Assert.Equal(T0, run.UpdatedAt);
        Assert.Matches("^[0-9a-f]{8}$", run.ShortId);
        Assert.Equal(new RunCreated(eventId, "github:o/r#1"), created);
        Assert.Equal(new StateChanged(null, RunState.Received), initial);
    }

    [Fact]
    public void ShortIds_of_runs_created_in_the_same_instant_differ()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => PipelineRun.Create(Guid.NewGuid(), "k", T0).Run.ShortId).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void State_has_no_public_setter()
    {
        var setter = typeof(PipelineRun).GetProperty(nameof(PipelineRun.State))!.SetMethod;

        Assert.True(setter is null || !setter.IsPublic);
    }

    [Fact]
    public void Fail_rejects_an_unknown_reason()
    {
        var run = RunIn(RunState.Received);

        Assert.Throws<ArgumentOutOfRangeException>(() => run.Fail((FailureReason)999, null));
        Assert.Equal(RunState.Received, run.State);
    }

    /// <summary>Lleva una Ejecución nueva a <paramref name="state"/> solo con transiciones válidas.</summary>
    private static PipelineRun RunIn(RunState state)
    {
        var run = PipelineRun.Create(Guid.CreateVersion7(), "github:o/r#1", T0).Run;
        switch (state)
        {
            case RunState.Received:
                break;
            case RunState.Analyzing:
                run.StartAnalysis();
                break;
            case RunState.Coding:
                run.StartAnalysis();
                run.StartCoding();
                break;
            case RunState.Reviewing:
                run.StartAnalysis();
                run.StartCoding();
                run.StartReview();
                break;
            case RunState.Deciding:
                run.StartAnalysis();
                run.StartCoding();
                run.StartReview();
                run.StartDeciding();
                break;
            case RunState.PullRequestCreated:
                run.StartAnalysis();
                run.StartCoding();
                run.StartReview();
                run.StartDeciding();
                run.CompletePullRequest();
                break;
            case RunState.Failed:
                run.Fail(FailureReason.StageError, null);
                break;
            case RunState.Cancelled:
                run.Cancel();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }

        Assert.Equal(state, run.State);
        return run;
    }
}
