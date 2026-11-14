using System;
using System.Runtime.InteropServices;
using GTA;
using GTA.Math;
using GTA.Native;

/// <summary>
/// SurvivalMoves - auto-starting ScriptHookVDotNet3 script.
/// No menu, no enable/disable key, and no control-learning/debug UI.
/// </summary>
public sealed class SurvivalMoves : Script
{
    private const int ControlGroup = 0;

    // GTA V controls used by the original SurvivalMoves input scheme.
    private const int AimControl = 25;       // LT / Aim
    private const int CoverControl = 44;     // RB / Cover
    private const int WeaponControl = 37;    // LB / Weapon selection
    private const int SprintControl = 21;    // A / Sprint
    private const int StealthControl = 36;   // L3 / Stealth (as shown by the supplied touch layout)

    // Additional controls and keyboard fallbacks used by the original DLL.
    private const int BandageDirectControl = 174;
    private const int ArmorDirectControl = 175;
    private const int BandageVirtualKey = 0x55; // U
    private const int ArmorVirtualKey = 0x56;   // V

    private const int BandageTotal = 60;
    private const int BandageStep = 10;
    private const int BandageTickMs = 500;
    private const int BandageDurationMs = 3000;
    private const int ArmorDurationMs = 1800;
    private const int SlideDurationMs = 900;
    private const int DoubleTapMs = 600;

    private const float SlideSpeed = 8.0f;
    private const float SlideEndSpeed = 1.5f;

    private const string BandageIdleDict = "amb@medic@standing@kneel@idle_a";
    private const string BandageIdleAnim = "idle_a";
    private const string BandageFallbackDict = "amb@medic@standing@tendtodead@base";
    private const string BandageFallbackAnim = "base";
    private const string ArmorDict = "clothingtie";
    private const string ArmorAnim = "try_tie_positive_a";
    private const string SlideDict = "missheistfbi3b_ig6_v2";
    private const string SlideAnim = "rubble_slide_franklin";

    private readonly int[] _bandageIds = { CoverControl };
    private readonly int[] _armorIds = { WeaponControl };
    private readonly int[] _slideIds = { StealthControl };

    private bool _previousU;
    private bool _previousV;

    private bool _bandaging;
    private int _healRemaining;
    private int _nextHealAt;
    private int _bandageEndsAt;

    private bool _armoring;
    private int _armorEndsAt;
    private bool _pendingArmor;
    private int _lastArmorTapAt;

    private bool _sliding;
    private int _slideStartedAt;
    private Vector3 _slideDirection;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    public SurvivalMoves()
    {
        // SHVDN loads the script with the game; Tick keeps it running automatically.
        Tick += OnTick;
        Aborted += OnAborted;
    }

    private static int Now => Game.GameTime;

    private static bool IsKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static bool IsControlDown(int control)
    {
        return Function.Call<bool>(Hash.IS_CONTROL_PRESSED, ControlGroup, control);
    }

    private static bool IsControlJustPressed(int control)
    {
        return Function.Call<bool>(Hash.IS_CONTROL_JUST_PRESSED, ControlGroup, control);
    }

    private static void DisableControl(int control)
    {
        Function.Call(Hash.DISABLE_CONTROL_ACTION, ControlGroup, control, true);
    }

    private static void DisableControls(int[] controls)
    {
        foreach (int control in controls)
            DisableControl(control);
    }

    private static bool AnyJustPressed(int[] controls)
    {
        foreach (int control in controls)
        {
            if (IsControlJustPressed(control))
                return true;
        }
        return false;
    }

    private static bool LoadAnimationDictionary(string dictionary)
    {
        Function.Call(Hash.REQUEST_ANIM_DICT, dictionary);
        for (int i = 0; i < 50; i++)
        {
            if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dictionary))
                return true;
            Script.Yield();
        }
        return Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dictionary);
    }

    private static bool PlayAnimation(Ped ped, string dictionary, string animation,
        int durationMs, int flags)
    {
        if (ped == null || !ped.Exists() || !LoadAnimationDictionary(dictionary))
            return false;

        Function.Call(Hash.TASK_PLAY_ANIM, ped, dictionary, animation,
            8.0f, -8.0f, durationMs, flags, 0.0f, false, false, false);
        return true;
    }

    private static void StopAnimation(Ped ped, string dictionary, string animation)
    {
        if (ped == null || !ped.Exists())
            return;

        Function.Call(Hash.STOP_ANIM_TASK, ped, dictionary, animation, 1.0f);
    }

    private static void StopBandageAnimations(Ped ped)
    {
        StopAnimation(ped, BandageIdleDict, BandageIdleAnim);
        StopAnimation(ped, BandageFallbackDict, BandageFallbackAnim);
    }

    private void StartBandage(Ped ped, int now)
    {
        if (_bandaging || _sliding || ped.Health >= ped.MaxHealth)
            return;

        _bandaging = true;
        _healRemaining = BandageTotal;
        _nextHealAt = now + BandageTickMs;
        _bandageEndsAt = now + BandageDurationMs;

        // Prefer idle_a; use the original tend-to-dead base animation only as fallback.
        if (!PlayAnimation(ped, BandageIdleDict, BandageIdleAnim,
            BandageDurationMs, 49))
        {
            PlayAnimation(ped, BandageFallbackDict, BandageFallbackAnim,
                BandageDurationMs, 49);
        }
    }

    private void StartArmor(Ped ped, int now)
    {
        if (_armoring || _sliding || ped.Armor >= 100)
            return;

        _armoring = true;
        _armorEndsAt = now + ArmorDurationMs;
        PlayAnimation(ped, ArmorDict, ArmorAnim, ArmorDurationMs, 49);
    }

    private void StartSlide(Ped ped, int now)
    {
        if (_sliding || ped == null || !ped.Exists() || !ped.IsSprinting)
            return;

        if (_armoring)
        {
            _armoring = false;
            StopAnimation(ped, ArmorDict, ArmorAnim);
        }

        if (_bandaging)
        {
            _bandaging = false;
            StopBandageAnimations(ped);
        }

        _sliding = true;
        _slideStartedAt = now;
        _slideDirection = ped.ForwardVector;

        // Correct slide animation from the user's ThaiMoves reference DLL.
        PlayAnimation(ped, SlideDict, SlideAnim, SlideDurationMs, 0);
    }

    private void UpdateBandage(Ped ped, int now)
    {
        if (!_bandaging)
            return;

        if (now >= _nextHealAt && _healRemaining > 0)
        {
            ped.Health = Math.Min(ped.MaxHealth, ped.Health + BandageStep);
            _healRemaining -= BandageStep;
            _nextHealAt = now + BandageTickMs;
        }

        if (_healRemaining <= 0 || now >= _bandageEndsAt || ped.Health >= ped.MaxHealth)
        {
            _bandaging = false;
            StopBandageAnimations(ped);
        }
    }

    private void UpdateArmor(Ped ped, int now)
    {
        if (!_armoring || now < _armorEndsAt)
            return;

        _armoring = false;
        ped.Armor = 100;
        StopAnimation(ped, ArmorDict, ArmorAnim);
    }

    private void UpdateSlide(Ped ped, int now)
    {
        if (!_sliding)
            return;

        int elapsed = now - _slideStartedAt;
        if (elapsed >= SlideDurationMs)
        {
            _sliding = false;
            StopAnimation(ped, SlideDict, SlideAnim);
            return;
        }

        float progress = Math.Max(0.0f, Math.Min(1.0f,
            (float)elapsed / SlideDurationMs));
        float speed = SlideSpeed * (1.0f - progress) + SlideEndSpeed;
        Vector3 velocity = _slideDirection * speed;
        velocity.Z = ped.Velocity.Z;
        ped.Velocity = velocity;
    }

    private void OnTick(object sender, EventArgs e)
    {
        Ped ped = Game.Player.Character;
        if (ped == null || !ped.Exists() || ped.IsDead || ped.IsInVehicle())
        {
            _sliding = false;
            _bandaging = false;
            _armoring = false;
            _pendingArmor = false;
            return;
        }

        int now = Now;
        bool uDown = IsKeyDown(BandageVirtualKey);
        bool vDown = IsKeyDown(ArmorVirtualKey);
        bool uPressed = uDown && !_previousU;
        bool vPressed = vDown && !_previousV;
        _previousU = uDown;
        _previousV = vDown;

        bool unarmed = ped.Weapons.Current.Hash == WeaponHash.Unarmed;
        bool aimModifier = unarmed && IsControlDown(AimControl);
        bool sprinting = ped.IsSprinting;

        // Consume the normal game actions when they are being used as modifiers.
        if (aimModifier)
            DisableControls(_bandageIds.Length > 0 && _armorIds.Length > 0
                ? new[] { CoverControl, WeaponControl }
                : Array.Empty<int>());
        if (sprinting)
            DisableControls(_slideIds);

        bool bandageCommand = IsControlJustPressed(BandageDirectControl) || uPressed;
        bool armorCommand = IsControlJustPressed(ArmorDirectControl) || vPressed;
        bool bandageCombo = aimModifier && AnyJustPressed(_bandageIds);
        bool armorCombo = aimModifier && AnyJustPressed(_armorIds);
        bool slideCombo = sprinting && AnyJustPressed(_slideIds);

        if (bandageCombo)
            bandageCommand = true;

        if (bandageCommand)
            StartBandage(ped, now);

        // Aim + Weapon starts armor immediately. The V/side-button path retains
        // the original double-tap-to-slide behavior while sprinting.
        if (armorCombo)
        {
            _pendingArmor = false;
            _lastArmorTapAt = 0;
            StartArmor(ped, now);
        }
        else if (armorCommand)
        {
            if (sprinting)
            {
                if (_lastArmorTapAt > 0 && now - _lastArmorTapAt < DoubleTapMs)
                {
                    _pendingArmor = false;
                    _lastArmorTapAt = 0;
                    StartSlide(ped, now);
                }
                else
                {
                    _lastArmorTapAt = now;
                    _pendingArmor = true;
                }
            }
            else
            {
                _pendingArmor = false;
                _lastArmorTapAt = 0;
                StartArmor(ped, now);
            }
        }

        if (slideCombo)
        {
            _pendingArmor = false;
            _lastArmorTapAt = 0;
            StartSlide(ped, now);
        }

        if (_pendingArmor && now - _lastArmorTapAt > DoubleTapMs)
        {
            _pendingArmor = false;
            _lastArmorTapAt = 0;
            if (!_sliding)
                StartArmor(ped, now);
        }

        UpdateBandage(ped, now);
        UpdateArmor(ped, now);
        UpdateSlide(ped, now);
    }

    private void OnAborted(object sender, EventArgs e)
    {
        Ped ped = Game.Player.Character;
        if (ped != null && ped.Exists())
        {
            StopBandageAnimations(ped);
            StopAnimation(ped, ArmorDict, ArmorAnim);
            StopAnimation(ped, SlideDict, SlideAnim);
        }
    }
}
