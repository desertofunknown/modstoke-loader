// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;

namespace BepInEx.Logging;

/// <summary>
///     Dispatches log events using the compatibility logger's listener snapshot.
/// </summary>
public interface ILogEventDispatcher
{
    /// <summary>
    ///     Applies listener filters and delivers an event on the source's calling thread.
    /// </summary>
    /// <remarks>
    ///     The snapshot preserves registration order and duplicate entries. Do not modify or retain it.
    ///     Collection changes during dispatch affect later events. This method can be called concurrently.
    /// </remarks>
    /// <param name="sender">The original event sender.</param>
    /// <param name="eventArgs">The original log event.</param>
    /// <param name="listeners">The borrowed listener snapshot for this event.</param>
    void Dispatch(object sender, LogEventArgs eventArgs, IReadOnlyList<ILogListener> listeners);
}
