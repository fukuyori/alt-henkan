using AltHenkan;

internal static class KeyHoldPolicyTests
{
    public static void Run(Action<string, bool> check)
    {
        const nuint japanese = 0x04110411;
        const nuint english = 0x04090409;

        foreach (var key in new uint[] { 0xF3, 0xF4 })
        {
            // Reproduce a width command whose release was never delivered and whose OS state remains down.
            var tracked = new HashSet<uint> { key };
            var down = new HashSet<uint> { key };
            check($"Japanese width command 0x{key:X2} bypasses gesture handling",
                KeyHoldPolicy.IsImeWidthCommand(key, japanese));
            KeyHoldPolicy.PruneNonAltKeys(tracked, japanese, down.Contains);
            check($"Latched width command 0x{key:X2} no longer blocks standalone Alt / deferred Ctrl",
                tracked.Count == 0);
            check($"Latched width command 0x{key:X2} permits due idle renewal",
                HookMaintenancePolicy.ShouldRefresh(30_000, 1000,
                    KeyHoldPolicy.AnyKeyOrMouseButtonDown(japanese, down.Contains), false, false));

            foreach (var layout in new nuint[] { english, 0 })
            {
                tracked.Add(key);
                KeyHoldPolicy.PruneNonAltKeys(tracked, layout, down.Contains);
                check($"OEM key 0x{key:X2} is retained for non-Japanese / unknown layout 0x{layout:X}",
                    !KeyHoldPolicy.IsImeWidthCommand(key, layout) && tracked.Contains(key) &&
                    KeyHoldPolicy.AnyKeyOrMouseButtonDown(layout, down.Contains));
            }
        }

        // VK_KANJI/VK_HANJA can remain in the down state after an IME chord.
        {
            const uint kanji = 0x19;
            var tracked = new HashSet<uint> { kanji };
            var down = new HashSet<uint> { kanji };
            check("Japanese VK_KANJI is treated as an IME hold artifact",
                KeyHoldPolicy.IsImeHoldArtifact(kanji, japanese));
            KeyHoldPolicy.PruneNonAltKeys(tracked, japanese, down.Contains);
            check("Latched Japanese VK_KANJI no longer blocks standalone Alt", tracked.Count == 0);
            check("Latched Japanese VK_KANJI permits due idle hook renewal",
                HookMaintenancePolicy.ShouldRefresh(30_000, 1000,
                    KeyHoldPolicy.AnyKeyOrMouseButtonDown(japanese, down.Contains), false, false));

            foreach (var layout in new nuint[] { english, 0 })
            {
                tracked.Add(kanji);
                KeyHoldPolicy.PruneNonAltKeys(tracked, layout, down.Contains);
                check($"VK_KANJI remains a normal held key for layout 0x{layout:X}",
                    !KeyHoldPolicy.IsImeHoldArtifact(kanji, layout) && tracked.Contains(kanji) &&
                    KeyHoldPolicy.AnyKeyOrMouseButtonDown(layout, down.Contains));
            }
        }

        // Physical F3/F4, ordinary letters, punctuation, modifiers, mouse buttons and neighboring
        // OEM/IME values must still delay renewal, except the known Japanese VK_KANJI artifact.
        foreach (var key in new uint[] { 0x72, 0x73, 0x41, 0xBF, 0x09, 0x10, 0x11, 0x12,
            0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x01, 0x02, 0x04, 0x05, 0x06, 0xF2, 0xF5 })
        {
            var down = new HashSet<uint> { key, 0xF3, 0xF4 };
            check($"Real held key/button 0x{key:X2} still blocks renewal alongside latched IME commands",
                !KeyHoldPolicy.IsImeWidthCommand(key, japanese) &&
                !HookMaintenancePolicy.ShouldRefresh(30_000, 1000,
                    KeyHoldPolicy.AnyKeyOrMouseButtonDown(japanese, down.Contains), false, true));
            var tracked = new HashSet<uint> { key, 0xF3, 0xF4 };
            KeyHoldPolicy.PruneNonAltKeys(tracked, japanese, down.Contains);
            check($"Real tracked key 0x{key:X2} remains after pruning IME commands", tracked.SetEquals([key]));
        }

        var released = new HashSet<uint> { 0x41, 0xF3 };
        KeyHoldPolicy.PruneNonAltKeys(released, japanese, _ => false);
        check("Missed ordinary key-up pruning remains supported", released.Count == 0);
        check("A retained Alt / Emacs gesture still prevents renewal",
            !HookMaintenancePolicy.ShouldRefresh(30_000, 1000,
                KeyHoldPolicy.AnyKeyOrMouseButtonDown(japanese, key => key == 0xF3), true, true));
        check("Recent input still prevents renewal despite ignored width state",
            !HookMaintenancePolicy.ShouldRefresh(30_000, 999,
                KeyHoldPolicy.AnyKeyOrMouseButtonDown(japanese, key => key == 0xF3), false, true));
    }
}
