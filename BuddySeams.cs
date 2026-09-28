namespace HatchAI
{
    // The two seams BuddyController (E4) talks through, so it can be driven in
    // a test with fakes — the FakeChatSession pattern — and so E2 and E3 can
    // build their halves without waiting on E4's.

    // Persistence of BuddyState into the top-level "buddy" object in
    // settings.json. Implemented by E2's BuddyStore.
    //
    // The "buddy" object is edited in place as a JsonObject, never rebuilt
    // from BuddyState, so a nested key a newer build wrote survives a save by
    // this one. (HatchAISettings holds the whole file as a JsonObject, so the
    // same holds at every other depth too.)
    internal interface IBuddyStore
    {
        // Null when there is no buddy yet (first run, or settings.json gone).
        BuddyState? Load();

        // One atomic write: counts and cursors together, or neither.
        void Save(BuddyState state);
    }

    // The companion window, its bubble and its card. Implemented by E3's
    // BuddyWindow. Every call is on the UI thread.
    internal interface IBuddyView
    {
        void Show(BuddyGenome genome, BuddyState state);

        // Redraws from a new state — tokens credited, a rebirth landed —
        // without showing the companion if it is hidden. Show also refreshes
        // when the companion is already up; this is the call for when it may
        // not be, so crediting tokens never brings back a buddy the user hid.
        // Added by E3 at E4's request (CB-195).
        void UpdateState(BuddyGenome genome, BuddyState state);

        void Hide();
        void ShowBubble(string text);
        void HideBubble();

        // The user accepted the rebirth offer on the card. The view does not
        // re-roll anything itself; the controller does, then calls Show.
        event Action? RebirthRequested;

        // The user put the buddy away from the buddy's own menu, which has
        // already turned the "Show buddy" preference off. The controller has
        // to hear about it: until it does it still believes the buddy is on
        // screen, keeps counting, and — because Reapply is a no-op when
        // nothing seems to have changed — ignores the tray or settings switch
        // that tries to bring it back (QA on CB-195). On the seam rather than
        // only on BuddyWindow so a controller cannot be built without it.
        event Action? HideRequested;
    }
}
