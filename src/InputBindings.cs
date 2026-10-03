using MelonLoader;
using ModsPanel;
using SK.Libretro.Header;
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace BR_Libretro;

internal static class InputBindings
{
    private const string OwnerId = "com.midgetbrony.br-libretro.inputs";

    private sealed class Binding
    {
        internal Binding(RETRO_DEVICE_ID_JOYPAD button, string id, string label,
            string defaultKeyboard, string defaultGamepad)
        {
            Button = button;
            Id = id;
            Label = label;
            DefaultKeyboard = defaultKeyboard;
            DefaultGamepad = defaultGamepad;
        }

        internal RETRO_DEVICE_ID_JOYPAD Button { get; }
        internal string Id { get; }
        internal string Label { get; }
        internal string DefaultKeyboard { get; }
        internal string DefaultGamepad { get; }
        internal MelonPreferences_Entry<string> KeyboardEntry { get; set; }
        internal MelonPreferences_Entry<string> GamepadEntry { get; set; }
    }

    private sealed class Capture
    {
        internal Binding Binding;
        internal bool IsKeyboard;
        internal bool IsPowerKey;
        internal bool WaitingForRelease = true;
    }

    private static readonly Binding[] Bindings =
    {
        new(RETRO_DEVICE_ID_JOYPAD.UP, "Up", "D-Pad Up", Key.UpArrow.ToString(), "DpadUp"),
        new(RETRO_DEVICE_ID_JOYPAD.DOWN, "Down", "D-Pad Down", Key.DownArrow.ToString(), "DpadDown"),
        new(RETRO_DEVICE_ID_JOYPAD.LEFT, "Left", "D-Pad Left", Key.LeftArrow.ToString(), "DpadLeft"),
        new(RETRO_DEVICE_ID_JOYPAD.RIGHT, "Right", "D-Pad Right", Key.RightArrow.ToString(), "DpadRight"),
        new(RETRO_DEVICE_ID_JOYPAD.A, "A", "A", Key.X.ToString(), "East"),
        new(RETRO_DEVICE_ID_JOYPAD.B, "B", "B", Key.Z.ToString(), "South"),
        new(RETRO_DEVICE_ID_JOYPAD.X, "X", "X", Key.S.ToString(), "North"),
        new(RETRO_DEVICE_ID_JOYPAD.Y, "Y", "Y", Key.A.ToString(), "West"),
        new(RETRO_DEVICE_ID_JOYPAD.START, "Start", "Start", Key.Enter.ToString(), "Start"),
        new(RETRO_DEVICE_ID_JOYPAD.SELECT, "Select", "Select", Key.RightShift.ToString(), "Select"),
        new(RETRO_DEVICE_ID_JOYPAD.L, "L", "L Shoulder", Key.None.ToString(), "LeftShoulder"),
        new(RETRO_DEVICE_ID_JOYPAD.R, "R", "R Shoulder", Key.None.ToString(), "RightShoulder"),
        new(RETRO_DEVICE_ID_JOYPAD.L2, "L2", "L Trigger", Key.None.ToString(), "LeftTrigger"),
        new(RETRO_DEVICE_ID_JOYPAD.R2, "R2", "R Trigger", Key.None.ToString(), "RightTrigger"),
        new(RETRO_DEVICE_ID_JOYPAD.L3, "L3", "L Stick Click", Key.None.ToString(), "LeftStick"),
        new(RETRO_DEVICE_ID_JOYPAD.R3, "R3", "R Stick Click", Key.None.ToString(), "RightStick")
    };

    private static MelonPreferences_Entry<string> powerKey;
    private static Capture capture;

    internal static void Initialize(MelonPreferences_Category preferences,
        MelonPreferences_Entry<string> powerKeyEntry)
    {
        powerKey = powerKeyEntry;
        foreach (Binding binding in Bindings)
        {
            binding.KeyboardEntry = preferences.CreateEntry(
                "Keyboard" + binding.Id, binding.DefaultKeyboard,
                "Keyboard binding: " + binding.Label);
            binding.GamepadEntry = preferences.CreateEntry(
                "Gamepad" + binding.Id, binding.DefaultGamepad,
                "Gamepad binding: " + binding.Label);
        }
    }

    internal static bool IsPressed(RETRO_DEVICE_ID_JOYPAD button,
        Keyboard keyboard, Gamepad gamepad)
    {
        Binding binding = Find(button);
        if (binding == null) return false;
        return IsKeyboardPressed(keyboard, binding.KeyboardEntry?.Value)
            || IsGamepadPressed(gamepad, binding.GamepadEntry?.Value);
    }

    internal static bool IsPowerPressed()
    {
        string configured = powerKey?.Value;
        if (string.Equals(configured, Key.None.ToString(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!Enum.TryParse(configured, true, out Key key) || key == Key.None)
            key = Key.F2;
        return Keyboard.current != null && Keyboard.current[key].isPressed;
    }

    internal static void UpdateCapture()
    {
        if (capture == null) return;

        Keyboard keyboard = Keyboard.current;
        Gamepad gamepad = Gamepad.current;
        if (keyboard?.escapeKey.wasPressedThisFrame == true)
        {
            CancelCapture("Binding cancelled.");
            return;
        }

        if (capture.WaitingForRelease)
        {
            if (!AnySupportedControlPressed(keyboard, gamepad))
            {
                capture.WaitingForRelease = false;
                ModsUi.ShowToast(capture.IsKeyboard
                    ? "Press a keyboard key. Backspace clears the binding."
                    : "Press a gamepad button.");
            }
            return;
        }

        if (capture.IsKeyboard)
        {
            if (keyboard?.backspaceKey.wasPressedThisFrame == true)
            {
                CompleteCapture(Key.None.ToString());
                return;
            }
            if (keyboard == null) return;
            foreach (KeyControl key in keyboard.allKeys)
            {
                if (!key.wasPressedThisFrame) continue;
                CompleteCapture(key.keyCode.ToString());
                return;
            }
            return;
        }

        if (gamepad == null) return;
        foreach (GamepadButton button in SupportedGamepadButtons)
        {
            if (!gamepad[button].wasPressedThisFrame) continue;
            CompleteCapture(GamepadName(button));
            return;
        }
    }

    internal static void OpenMenu()
    {
        capture = null;
        ModsUi.CreateMenu(OwnerId, "BR-Libretro Controls",
                "Configure player-one keyboard and gamepad bindings.")
            .AddButton("Keyboard bindings", OpenKeyboardMenu,
                "Power key and all RetroPad buttons")
            .AddButton("Gamepad bindings", OpenGamepadMenu,
                "RetroPad buttons; analog sticks remain automatic")
            .AddButton("Reset all defaults", ResetDefaults,
                "Restores the original keyboard and gamepad layout")
            .AddLabel("Bindings are saved in UserData/MelonPreferences.cfg. Existing defaults are preserved until you change them.")
            .Show();
    }

    internal static void CancelCapture(string message = null)
    {
        capture = null;
        if (!string.IsNullOrWhiteSpace(message)) ModsUi.ShowToast(message);
    }

    private static void OpenKeyboardMenu()
    {
        capture = null;
        ModMenu menu = ModsUi.CreateMenu(OwnerId + ".keyboard", "Keyboard Bindings",
            "Select an action, release the current key, then press its replacement.");
        menu.AddButton("TV focus / power", () => BeginCapture(null, true, true),
            Display(powerKey?.Value));
        foreach (Binding item in Bindings)
        {
            Binding binding = item;
            menu.AddButton(binding.Label, () => BeginCapture(binding, true, false),
                Display(binding.KeyboardEntry?.Value));
        }
        menu.AddButton("Back", OpenMenu);
        menu.AddLabel("Escape cancels capture. Backspace clears the selected keyboard binding.");
        menu.Closed = () => CancelCapture();
        menu.Show();
    }

    private static void OpenGamepadMenu()
    {
        capture = null;
        ModMenu menu = ModsUi.CreateMenu(OwnerId + ".gamepad", "Gamepad Bindings",
            "Select an action, release the current button, then press its replacement.");
        foreach (Binding item in Bindings)
        {
            Binding binding = item;
            menu.AddButton(binding.Label, () => BeginCapture(binding, false, false),
                Display(binding.GamepadEntry?.Value));
        }
        menu.AddButton("Back", OpenMenu);
        menu.AddLabel("The first active gamepad controls player one. Escape cancels capture.");
        menu.Closed = () => CancelCapture();
        menu.Show();
    }

    private static void BeginCapture(Binding binding, bool keyboard, bool isPowerKey)
    {
        capture = new Capture
        {
            Binding = binding,
            IsKeyboard = keyboard,
            IsPowerKey = isPowerKey,
            WaitingForRelease = true
        };
        string label = isPowerKey ? "TV focus / power" : binding?.Label ?? "control";
        ModsUi.ShowToast("Release controls, then press the new " + label + " binding.", 6f);
    }

    private static void CompleteCapture(string value)
    {
        Capture completed = capture;
        capture = null;
        if (completed == null) return;

        if (completed.IsPowerKey)
            powerKey.Value = value;
        else if (completed.IsKeyboard)
            completed.Binding.KeyboardEntry.Value = value;
        else
            completed.Binding.GamepadEntry.Value = value;

        MelonPreferences.Save();
        ModsUi.ShowToast("Bound " + (completed.IsPowerKey ? "TV focus / power" : completed.Binding.Label)
            + " to " + Display(value) + ".");
        if (completed.IsKeyboard) OpenKeyboardMenu(); else OpenGamepadMenu();
    }

    private static void ResetDefaults()
    {
        if (powerKey != null) powerKey.Value = Key.F2.ToString();
        foreach (Binding binding in Bindings)
        {
            binding.KeyboardEntry.Value = binding.DefaultKeyboard;
            binding.GamepadEntry.Value = binding.DefaultGamepad;
        }
        MelonPreferences.Save();
        ModsUi.ShowToast("BR-Libretro controls restored to defaults.");
        OpenMenu();
    }

    private static Binding Find(RETRO_DEVICE_ID_JOYPAD button)
    {
        foreach (Binding binding in Bindings)
            if (binding.Button == button) return binding;
        return null;
    }

    private static bool IsKeyboardPressed(Keyboard keyboard, string value)
    {
        if (keyboard == null || !Enum.TryParse(value, true, out Key key) || key == Key.None)
            return false;
        return keyboard[key].isPressed;
    }

    private static bool IsGamepadPressed(Gamepad gamepad, string value)
    {
        if (gamepad == null || !Enum.TryParse(value, true, out GamepadButton button))
            return false;
        return gamepad[button].isPressed;
    }

    private static bool AnySupportedControlPressed(Keyboard keyboard, Gamepad gamepad)
    {
        if (keyboard?.anyKey.isPressed == true) return true;
        if (gamepad == null) return false;
        foreach (GamepadButton button in SupportedGamepadButtons)
            if (gamepad[button].isPressed) return true;
        return false;
    }

    private static string Display(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals(Key.None.ToString(), StringComparison.OrdinalIgnoreCase)
            ? "Unbound"
            : value;

    private static string GamepadName(GamepadButton button)
    {
        if (button == GamepadButton.North) return "North";
        if (button == GamepadButton.East) return "East";
        if (button == GamepadButton.South) return "South";
        if (button == GamepadButton.West) return "West";
        return button.ToString();
    }

    private static readonly GamepadButton[] SupportedGamepadButtons =
    {
        GamepadButton.DpadUp, GamepadButton.DpadDown, GamepadButton.DpadLeft, GamepadButton.DpadRight,
        GamepadButton.North, GamepadButton.East, GamepadButton.South, GamepadButton.West,
        GamepadButton.LeftShoulder, GamepadButton.RightShoulder,
        GamepadButton.LeftTrigger, GamepadButton.RightTrigger,
        GamepadButton.LeftStick, GamepadButton.RightStick,
        GamepadButton.Start, GamepadButton.Select
    };
}
