namespace RetainerPricer;

/// <summary>
/// Drives the game's own retainer UI for a user-started venture cycle. This controller has no
/// AutoRetainer reference, IPC, or runtime dependency.
/// </summary>
internal sealed class VentureController(NativeMarketBridge bridge, PluginConfig config)
{
    private enum Step
    {
        Idle,
        SelectRetainer,
        WaitForRetainerMenu,
        WaitForQuickExplorationMenu,
        WaitForResult,
        WaitForTaskAsk,
        WaitForPostAction,
        WaitForPicker
    }

    private static readonly TimeSpan ScreenTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ActionDelay = TimeSpan.FromMilliseconds(700);
    private readonly NativeMarketBridge bridge = bridge;
    private readonly PluginConfig config = config;
    private readonly List<RetainerIdentity> retainers = [];
    private Step step;
    private RetainerVentureMenuLabels? labels;
    private RetainerIdentity? currentRetainer;
    private ulong contentId;
    private uint worldId;
    private ulong previouslySelectedRetainerId;
    private int retainerIndex;
    private int dialogueClicks;
    private int processed;
    private int skipped;
    private DateTimeOffset nextActionAt;
    private DateTimeOffset deadline;
    private bool actionReassigns;

    public bool IsRunning => step != Step.Idle;
    public string Status { get; private set; } = "Open the retainer picker to start a venture cycle.";

    public void Start()
    {
        if (IsRunning) return;
        if (!config.RunVentures)
        { Status = "Enable Run ventures in the Ventures tab before starting the cycle."; return; }
        if (!bridge.IsRetainerPickerVisible)
        { Status = "Open the retainer picker at a summoning bell before starting a venture cycle."; return; }
        if (!bridge.TryGetCharacterContext(out contentId, out worldId, out var error))
        { Status = error; return; }
        if (!bridge.TryGetRetainerPickerOrder(out var pickerOrder, out error))
        { Status = error; return; }
        if (!bridge.TryGetRetainerVentureMenuLabels(out labels, out error))
        { Status = error; return; }
        if (pickerOrder.Count == 0)
        { Status = "No retainers were found in the open retainer picker."; return; }

        retainers.Clear();
        retainers.AddRange(pickerOrder);
        retainerIndex = 0;
        processed = 0;
        skipped = 0;
        currentRetainer = null;
        previouslySelectedRetainerId = 0;
        step = Step.SelectRetainer;
        nextActionAt = DateTimeOffset.UtcNow;
        Status = $"Venture cycle queued for {retainers.Count} retainer(s).";
    }

    public void Update()
    {
        if (!IsRunning) return;
        var now = DateTimeOffset.UtcNow;
        if (now < nextActionAt) return;
        if (bridge.IsClientStateUnavailable || bridge.IsCharacterOrWorldChanged(contentId, worldId))
        { Cancel("Venture cycle stopped because the character disconnected or changed world."); return; }

        switch (step)
        {
            case Step.SelectRetainer:
                SelectNextRetainer(now);
                break;
            case Step.WaitForRetainerMenu:
                WaitForRetainerMenu(now);
                break;
            case Step.WaitForQuickExplorationMenu:
                WaitForQuickExplorationMenu(now);
                break;
            case Step.WaitForResult:
                if (bridge.IsVentureTaskResultVisible)
                {
                    if (!VerifyCurrentRetainer(out var identityError))
                    {
                        if (now > deadline) Cancel($"Venture cycle stopped before handling the report: {identityError}");
                        return;
                    }
                    if (!bridge.TryClickVentureResult(actionReassigns, out var error))
                    { Cancel($"Venture cycle stopped for {currentRetainer?.Name}: {error}"); return; }
                    processed++;
                    if (actionReassigns)
                        SetStep(Step.WaitForTaskAsk, now, $"Repeating {currentRetainer?.Name}'s completed venture...");
                    else
                        SetStep(Step.WaitForPostAction, now, $"Collecting {currentRetainer?.Name}'s completed venture...");
                }
                else if (now > deadline)
                    Cancel($"Venture cycle stopped because {currentRetainer?.Name}'s venture report did not open.");
                break;
            case Step.WaitForTaskAsk:
                if (bridge.IsVentureTaskAskVisible)
                {
                    if (!VerifyCurrentRetainer(out var identityError))
                    {
                        if (now > deadline) Cancel($"Venture cycle stopped before confirming the assignment: {identityError}");
                        return;
                    }
                    if (!bridge.TryClickVentureAssign(out var error))
                    { Cancel($"Venture cycle stopped for {currentRetainer?.Name}: {error}"); return; }
                    processed++;
                    SetStep(Step.WaitForPostAction, now, $"Assigning the selected venture to {currentRetainer?.Name}...");
                }
                else if (now > deadline)
                    Cancel($"Venture cycle stopped because {currentRetainer?.Name}'s assignment confirmation did not open.");
                break;
            case Step.WaitForPostAction:
                WaitForPostAction(now);
                break;
            case Step.WaitForPicker:
                if (bridge.IsRetainerPickerVisible)
                {
                    retainerIndex++;
                    currentRetainer = null;
                    SetStep(Step.SelectRetainer, now, $"Retainer {retainerIndex} of {retainers.Count} complete.");
                }
                else if (now > deadline)
                    Cancel($"Venture cycle stopped because the retainer picker did not return after {currentRetainer?.Name}.");
                break;
        }

        if (IsRunning && now > deadline && step is Step.WaitForRetainerMenu or Step.WaitForQuickExplorationMenu)
            Cancel($"Venture cycle stopped because the expected retainer menu did not appear for {currentRetainer?.Name}.");
    }

    public void Cancel(string? message = null)
    {
        if (!IsRunning && message is null) return;
        step = Step.Idle;
        currentRetainer = null;
        Status = message ?? "Venture cycle stopped. The current game window was left open.";
    }

    private void SelectNextRetainer(DateTimeOffset now)
    {
        if (retainerIndex >= retainers.Count)
        {
            step = Step.Idle;
            currentRetainer = null;
            Status = $"Venture cycle complete: {processed} venture action(s), {skipped} retainer(s) skipped.";
            return;
        }
        if (!bridge.IsRetainerPickerVisible)
        { Cancel("Venture cycle stopped because the retainer picker closed unexpectedly."); return; }

        var target = retainers[retainerIndex];
        bridge.TryGetSelectedRetainerId(out previouslySelectedRetainerId);
        if (!bridge.TrySelectRetainerById(target, out var unavailable, out var error))
        {
            if (unavailable)
            {
                skipped++;
                retainerIndex++;
                Status = $"Skipped unavailable retainer {target.Name}.";
                return;
            }
            Cancel($"Venture cycle stopped before selecting {target.Name}: {error}");
            return;
        }
        currentRetainer = target;
        dialogueClicks = 0;
        SetStep(Step.WaitForRetainerMenu, now, $"Opening {target.Name}...");
    }

    private void WaitForRetainerMenu(DateTimeOffset now)
    {
        if (currentRetainer is not { } expected)
        { Cancel("Venture cycle lost its current retainer identity."); return; }
        if (bridge.IsRetainerMenuVisible)
        {
            if (!bridge.TryGetActiveRetainerVenture(expected, out var ventureId, out var error))
            {
                if (now <= deadline) return;
                Cancel($"Venture cycle stopped because {expected.Name} could not be verified: {error}");
                return;
            }
            InspectRetainerMenu(expected, ventureId, now);
            return;
        }
        if (bridge.IsRetainerDialogueVisible)
        {
            if (dialogueClicks >= 4)
            { Cancel($"Venture cycle stopped because {expected.Name}'s greeting did not advance."); return; }
            if (!bridge.TryAdvanceRetainerDialogue(expected, previouslySelectedRetainerId, out var error))
            { Cancel($"Venture cycle stopped while advancing {expected.Name}'s greeting: {error}"); return; }
            dialogueClicks++;
            nextActionAt = now + ActionDelay;
            deadline = now + ScreenTimeout;
            Status = $"Advancing {expected.Name}'s greeting ({dialogueClicks}/4)...";
            return;
        }
        if (bridge.IsRetainerPickerVisible)
        {
            skipped++;
            retainerIndex++;
            currentRetainer = null;
            SetStep(Step.SelectRetainer, now, $"Skipped {expected.Name}; its retainer menu did not open.");
        }
    }

    private void InspectRetainerMenu(RetainerIdentity expected, ushort ventureId, DateTimeOffset now)
    {
        if (labels is null)
        { Cancel("Venture cycle stopped because its localized game menu labels are unavailable."); return; }
        if (!bridge.TryRetainerMenuContains(labels.ViewReport, out var hasReport, out var error))
        { Cancel($"Venture cycle stopped while reading {expected.Name}'s menu: {error}"); return; }
        if (hasReport)
        {
            if (ventureId == 0)
            { Cancel($"Venture cycle stopped because {expected.Name}'s menu and venture data disagree."); return; }
            if (!bridge.TrySelectRetainerMenuEntry(text => string.Equals(text, labels.ViewReport, StringComparison.Ordinal), out _, out error))
            { Cancel($"Venture cycle stopped before opening {expected.Name}'s venture report: {error}"); return; }
            actionReassigns = config.RepeatCompletedVentures;
            SetStep(Step.WaitForResult, now, $"Opening {expected.Name}'s completed venture...");
            return;
        }

        if (ventureId == 0 && config.AssignQuickExplorationWhenIdle)
        {
            if (!bridge.TrySelectRetainerMenuEntry(text => labels.AssignOptions.Contains(text, StringComparer.Ordinal), out _, out error))
            { Cancel($"Venture cycle stopped before assigning a venture to {expected.Name}: {error}"); return; }
            SetStep(Step.WaitForQuickExplorationMenu, now, $"Choosing a venture for {expected.Name}...");
            return;
        }

        SelectQuit(expected, now, ventureId == 0
            ? $"Leaving {expected.Name} idle."
            : $"Leaving {expected.Name}'s venture in progress.");
    }

    private void WaitForQuickExplorationMenu(DateTimeOffset now)
    {
        if (currentRetainer is not { } expected || labels is null)
        { Cancel("Venture cycle lost its retainer or menu state while choosing a venture."); return; }
        if (bridge.IsRetainerMenuVisible)
        {
            if (!bridge.TryGetActiveRetainerVenture(expected, out var ventureId, out var error))
            {
                if (now <= deadline) return;
                Cancel($"Venture cycle stopped because {expected.Name} could not be verified: {error}");
                return;
            }
            if (ventureId != 0)
            { Cancel($"Venture cycle stopped because {expected.Name} already has a venture while the assignment menu is open."); return; }
            if (!bridge.TrySelectRetainerMenuEntry(text => string.Equals(text, labels.QuickExploration, StringComparison.Ordinal), out _, out error))
            {
                if (now <= deadline) return;
                Cancel($"Venture cycle stopped before choosing Quick Exploration for {expected.Name}: {error}");
                return;
            }
            SetStep(Step.WaitForTaskAsk, now, $"Confirming Quick Exploration for {expected.Name}...");
            return;
        }
        if (bridge.IsVentureTaskAskVisible)
        { Cancel($"Venture cycle stopped because {expected.Name}'s assignment screen opened before Quick Exploration could be verified."); return; }
    }

    private void WaitForPostAction(DateTimeOffset now)
    {
        if (currentRetainer is not { } expected)
        { Cancel("Venture cycle lost its current retainer after a venture action."); return; }
        if (bridge.IsVentureTaskResultVisible || bridge.IsVentureTaskAskVisible)
        {
            if (!VerifyCurrentRetainer(out var identityError) && now > deadline)
            { Cancel($"Venture cycle stopped because the active retainer changed: {identityError}"); return; }
            if (now > deadline)
                Cancel($"Venture cycle stopped because {expected.Name}'s venture window did not close after the action.");
            return;
        }
        if (bridge.IsRetainerMenuVisible)
        {
            if (!bridge.TryGetActiveRetainerVenture(expected, out var ventureId, out var error))
            {
                if (now <= deadline) return;
                Cancel($"Venture cycle stopped because {expected.Name} could not be verified after the venture action: {error}");
                return;
            }
            InspectRetainerMenu(expected, ventureId, now);
            return;
        }
        if (bridge.IsRetainerPickerVisible)
        {
            retainerIndex++;
            currentRetainer = null;
            SetStep(Step.SelectRetainer, now, $"Completed {expected.Name}; moving to the next retainer.");
            return;
        }
        if (now > deadline)
            Cancel($"Venture cycle stopped because {expected.Name}'s retainer menu did not return after the action.");
    }

    private void SelectQuit(RetainerIdentity expected, DateTimeOffset now, string status)
    {
        if (labels is null)
        { Cancel($"Venture cycle stopped before leaving {expected.Name}'s menu: localized menu labels are unavailable."); return; }
        if (!bridge.TrySelectRetainerMenuEntry(
                text => string.Equals(text, labels.Quit, StringComparison.Ordinal), out _, out var error))
        { Cancel($"Venture cycle stopped before leaving {expected.Name}'s menu: {error}"); return; }
        SetStep(Step.WaitForPicker, now, status);
    }

    private bool VerifyCurrentRetainer(out string error)
    {
        if (currentRetainer is not { } expected)
        { error = "The queued retainer identity is unavailable."; return false; }
        if (bridge.TryGetActiveRetainerVenture(expected, out _, out error)) return true;
        return false;
    }

    private void SetStep(Step next, DateTimeOffset now, string status)
    {
        step = next;
        nextActionAt = now + ActionDelay;
        deadline = now + ScreenTimeout;
        Status = status;
    }
}
