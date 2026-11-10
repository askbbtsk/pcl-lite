using System;
using System.Collections.Generic;
using System.Linq;
using PCL.Core.Utils;

namespace PCL;

internal interface IOperationTaskPresentation
{
    IReadOnlyList<ModLoader.LoaderBase> PresentedStages { get; }
}

/// <summary>Native task-card rows backed by actual operation phases and their measured progress.</summary>
internal sealed class OperationTaskPresentation
{
    private readonly Stage[] _stages;
    private readonly StagedOperationProgress _progress;
    private ModLoader.LoaderBase? _owner;
    private int _active = -1;
    private readonly string[] _titles;

    internal OperationTaskPresentation(params (string Name, double Weight)[] stages)
    {
        _progress = new(stages.Select(stage => stage.Weight).ToArray());
        _titles = stages.Select(stage => stage.Name).ToArray();
        _stages = stages.Select(stage => new Stage(stage.Name)).ToArray();
    }

    internal void Bind(ModLoader.LoaderBase owner) => _owner = owner;
    internal void Describe(int stage, string detail)
    {
        if (stage == _active) _stages[stage].name = _titles[stage] + " — " + detail;
    }
    internal void Begin(int stage)
    {
        if (_active >= 0 && _active != stage) Complete(_active);
        _progress.Begin(stage);
        _active = stage;
        _stages[stage].Start();
        Publish();
    }

    internal void Report(int stage, double fraction)
    {
        _progress.Report(stage, fraction);
        if (stage == _active) _stages[stage].Progress = Math.Max(_stages[stage].Progress, Math.Clamp(fraction, 0, 1));
        Publish();
    }

    internal void Complete(int stage)
    {
        _progress.Complete(stage);
        _stages[stage].Progress = 1;
        _stages[stage].State = ModBase.LoadState.Finished;
        Publish();
    }

    internal ModLoader.LoaderCombo<string> Wrap(string name, ModLoader.LoaderTask<string, string> operation)
    {
        operation.show = false;
        Bind(operation);
        return new PresentedCombo(name, operation, _stages);
    }

    private void Publish()
    {
        if (_owner is not null) _owner.Progress = _progress.Overall;
    }

    private sealed class PresentedCombo : ModLoader.LoaderCombo<string>, IOperationTaskPresentation
    {
        public IReadOnlyList<ModLoader.LoaderBase> PresentedStages { get; }
        internal PresentedCombo(string name, ModLoader.LoaderBase operation, Stage[] stages) : base(name, [operation])
        {
            show = true;
            PresentedStages = stages;
        }
    }

    private sealed class Stage : ModLoader.LoaderBase
    {
        internal Stage(string title) { name = title; }
        public override void Start(object? input = null, bool isForceRestart = false)
        {
            if (State == ModBase.LoadState.Waiting) State = ModBase.LoadState.Loading;
        }
        public override void Abort() { State = ModBase.LoadState.Aborted; }
    }
}
