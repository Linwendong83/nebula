#region

using System;
using System.Collections.Generic;
using NebulaModel.Authority;

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// Fans one <see cref="IHostWorldView"/> read out to several domain adapters (A08 + A12).
/// </summary>
/// <remarks>
/// Each domain adapter owns one pool kind and answers <c>false</c> ("cannot read") for every other
/// kind. The composite returns the first adapter that can read the scope and reports unreadable only
/// when none can — which keeps "unreadable is not empty" per-domain: a planet whose factory is not
/// loaded is skipped, never published as a wave of deaths, no matter how many domains exist.
/// </remarks>
public sealed class CompositeHostWorldView : IHostWorldView
{
    private readonly List<IHostWorldView> views = [];

    public CompositeHostWorldView(params IHostWorldView[] views)
    {
        if (views == null || views.Length == 0) throw new ArgumentException("At least one world view is required.", nameof(views));
        this.views.AddRange(views);
    }

    public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
    {
        foreach (var view in views)
        {
            var count = members.Count;
            if (view.TryReadMembers(scope, members))
            {
                return true;
            }
            if (members.Count != count)
            {
                members.RemoveRange(count, members.Count - count);
            }
        }
        return false;
    }

    public bool TryReadState(ObjectKey key, out byte[] state)
    {
        state = null;
        foreach (var view in views)
        {
            if (view.TryReadState(key, out state))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Fans one replica-mirror notification out to several domain bindings (A08 + A12).
/// </summary>
/// <remarks>
/// Each binding ignores scopes it does not own, so fan-out is safe: an Entity baseline reaches only
/// the factory combat binding and a GroundEnemy baseline only the ground binding. A throw in one
/// binding must not prevent the others from converging the same atomic baseline, so failures are
/// isolated per binding and counted rather than aborting the fan-out.
/// </remarks>
public sealed class CompositeMirrorObserver : IReplicaMirrorObserver
{
    private readonly List<IReplicaMirrorObserver> observers = [];

    public CompositeMirrorObserver(params IReplicaMirrorObserver[] observers)
    {
        if (observers == null || observers.Length == 0) throw new ArgumentException("At least one observer is required.", nameof(observers));
        this.observers.AddRange(observers);
    }

    public long ObserverFailures { get; private set; }

    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
    {
        foreach (var observer in observers)
        {
            try
            {
                observer.OnStateApplied(scope, key, revision, state);
            }
            catch (Exception)
            {
                ObserverFailures++;
            }
        }
    }

    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
    {
        foreach (var observer in observers)
        {
            try
            {
                observer.OnBaselineInstalled(scope, members);
            }
            catch (Exception)
            {
                ObserverFailures++;
            }
        }
    }

    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
    {
        foreach (var observer in observers)
        {
            try
            {
                observer.OnMemberRemoved(scope, key);
            }
            catch (Exception)
            {
                ObserverFailures++;
            }
        }
    }
}
