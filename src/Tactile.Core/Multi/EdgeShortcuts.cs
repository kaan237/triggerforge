using Tactile.Device.DualSense;

namespace Tactile.Core;

internal enum EdgeShortcut { Next, Previous, Profile1, Profile2, Profile3, Profile4, OnScreenKeyboard }

internal static class EdgeShortcuts
{
    public static EdgeShortcut? Read(ControllerModel model, Buttons previous, Buttons current)
    {
        if (model != ControllerModel.DualSenseEdge)
        {
            return null;
        }

        Buttons pressed = current & ~previous;
        bool PressedWith(Buttons fn, Buttons button) => (previous & current & fn) != 0 && (pressed & button) != 0;

        if (PressedWith(Buttons.Fn1, Buttons.R1)) return EdgeShortcut.Next;
        if (PressedWith(Buttons.Fn1, Buttons.L1)) return EdgeShortcut.Previous;
        if (PressedWith(Buttons.Fn1, Buttons.Triangle)) return EdgeShortcut.OnScreenKeyboard;
        if (PressedWith(Buttons.Fn2, Buttons.Triangle)) return EdgeShortcut.Profile1;
        if (PressedWith(Buttons.Fn2, Buttons.Circle)) return EdgeShortcut.Profile2;
        if (PressedWith(Buttons.Fn2, Buttons.Cross)) return EdgeShortcut.Profile3;
        if (PressedWith(Buttons.Fn2, Buttons.Square)) return EdgeShortcut.Profile4;
        return null;
    }

    public static Profile? Resolve(EdgeShortcut shortcut, IReadOnlyList<Profile> profiles, string currentId)
    {
        if (shortcut == EdgeShortcut.OnScreenKeyboard)
        {
            return null;
        }

        if (shortcut >= EdgeShortcut.Profile1)
        {
            int index = shortcut - EdgeShortcut.Profile1;
            return index < profiles.Count ? profiles[index] : null;
        }

        int current = 0;
        for (int i = 0; i < profiles.Count; i++)
        {
            if (profiles[i].Id == currentId)
            {
                current = i;
                break;
            }
        }

        int step = shortcut == EdgeShortcut.Next ? 1 : -1;
        return profiles[(current + step + profiles.Count) % profiles.Count];
    }

    public static Notice Changed(Profile profile, int index) => new Notice(NoticeKinds.Info,
        $"Profile: {profile.Name}", $"Profil: {profile.Name}") { Controller = index + 1 };
}
