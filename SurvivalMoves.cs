using System;
using System.Runtime.InteropServices;
using GTA;
using GTA.Math;
using GTA.Native;

// SurvivalMoves v4 - ผูกกับปุ่มที่พอร์ตมีอยู่แล้ว (ไม่ต้องมีปุ่มเพิ่มบนจอ)
//
//   พันแผล  = ค้าง Aim (LT) แล้วแตะ Cover (RB)
//   ใส่เกราะ = ค้าง Aim (LT) แล้วแตะปุ่มอาวุธ (LB)
//   สไลด์   = ตอนวิ่งอยู่ (Sprint) แตะ Stealth (L3)
//   (ตั้งค่าเริ่มต้น: ใช้คอมโบ LT ได้เฉพาะตอนมือเปล่า เพื่อไม่ชนกับตอนยิงปืน/เข้าที่กำบัง)
//   D-pad ซ้าย/ขวา (ถ้ามีจอย) ยังใช้ได้เหมือนเดิม
public class SurvivalMoves : Script
{
    // ---------- ผูกปุ่ม (ตัวเลขคือ Control ID ของเกม; ใส่ได้หลายตัว) ----------
    static readonly int[] MOD_IDS     = { (int)Control.Aim };          // LT  ค้างไว้
    static readonly int[] BANDAGE_IDS = { (int)Control.Cover };        // RB  แตะ
    static readonly int[] ARMOR_IDS   = { (int)Control.SelectWeapon }; // LB  แตะ
    static readonly int[] SLIDE_IDS   = { (int)Control.Duck };         // L3 / Stealth  แตะตอนวิ่ง

    const bool ONLY_WHEN_UNARMED = true;  // true = คอมโบ LT ใช้ได้เฉพาะตอนมือเปล่า
    const bool LEARN_MODE = true;         // true = แตะปุ่มแล้วขึ้นเลข Control ID บนจอ (ไว้เช็กว่าปุ่มพอร์ตส่งเลขอะไร) ใช้ได้แล้วตั้งเป็น false

    const Control BANDAGE_BTN = Control.PhoneLeft;   // D-pad ซ้าย
    const Control ARMOR_BTN   = Control.PhoneRight;  // D-pad ขวา
    const int BANDAGE_KEY = 0x75;   // F6
    const int ARMOR_KEY   = 0x76;   // F7

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    // ---------- ค่าต่างๆ ----------
    const int   DOUBLE_TAP_MS   = 600;
    const int   BANDAGE_TOTAL   = 60;
    const int   BANDAGE_STEP    = 10;
    const int   BANDAGE_TICK_MS = 500;
    const int   ARMOR_ANIM_MS   = 1800;
    const int   SLIDE_MS        = 900;
    const float SLIDE_SPEED     = 8f;

    const string BANDAGE_DICT = "amb@medic@standing@tendtodead@base";
    const string BANDAGE_ANIM = "base";
    const string ARMOR_DICT   = "clothingtie";
    const string ARMOR_ANIM   = "try_tie_positive_a";
    const string SLIDE_DICT   = "move_jump";
    const string SLIDE_ANIM   = "dive_start_run";
    // -------------------------------------------

    // Control ID ที่ใช้สแกนในโหมดเรียนรู้ (ข้ามแกนอนาล็อกที่ขึ้นรัว)
    static readonly int[] LEARN_IDS = BuildLearnIds();

    bool introShown; int introUntil;
    int learnUntil; string learnText = "";

    bool prevBandageKey, prevArmorKey;
    bool bandaging; int healLeft; int nextHeal; int bandageEnd;
    bool armoring; int armorEnd;
    bool pendingArmor; int lastTap;
    bool sliding; int slideStart; Vector3 slideDir;

    public SurvivalMoves()
    {
        Tick += OnTick;
    }

    static int[] BuildLearnIds()
    {
        var list = new System.Collections.Generic.List<int>();
        for (int i = 14; i <= 60; i++) list.Add(i);
        for (int i = 140; i <= 146; i++) list.Add(i);
        for (int i = 166; i <= 175; i++) list.Add(i);
        list.AddRange(new[] { 177, 178, 192, 193, 244, 245, 249, 257, 261, 262, 289, 290, 296, 297, 298, 299 });
        return list.ToArray();
    }

    bool Pressed(Control c) { return JustPressed((int)c); }

    bool JustPressed(int id)
    {
        return Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, id);
    }

    bool Held(int id)
    {
        return Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, id);
    }

    bool AnyJust(int[] ids)
    {
        for (int i = 0; i < ids.Length; i++) if (JustPressed(ids[i])) return true;
        return false;
    }

    bool AnyHeld(int[] ids)
    {
        for (int i = 0; i < ids.Length; i++) if (Held(ids[i])) return true;
        return false;
    }

    void DisableAll(int[] ids)
    {
        for (int i = 0; i < ids.Length; i++)
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, ids[i], true);
    }

    bool LoadDict(string dict)
    {
        Function.Call(Hash.REQUEST_ANIM_DICT, dict);
        int tries = 0;
        while (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict) && tries++ < 50)
            Script.Yield();
        return Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict);
    }

    void PlayAnim(Ped p, string dict, string anim, int ms, int flag)
    {
        if (!LoadDict(dict)) return;
        Function.Call(Hash.TASK_PLAY_ANIM, p, dict, anim, 8f, -8f, ms, flag, 0f, false, false, false);
    }

    void StopAnim(Ped p, string dict, string anim)
    {
        Function.Call(Hash.STOP_ANIM_TASK, p, dict, anim, 1f);
    }

    void Learn(int now)
    {
        string found = "";
        for (int i = 0; i < LEARN_IDS.Length; i++)
            if (JustPressed(LEARN_IDS[i])) found += (found.Length > 0 ? ", " : "") + LEARN_IDS[i];
        if (found.Length > 0) { learnText = "ctrl " + found; learnUntil = now + 2500; }
        if (now < learnUntil) GTA.UI.Screen.ShowSubtitle(learnText, 100);
    }

    void OnTick(object sender, EventArgs e)
    {
        Ped p = Game.Player.Character;
        bool alive = p != null && p.Exists() && !p.IsDead;
        int now = Game.GameTime;
        if (!alive) { sliding = false; return; }

        if (LEARN_MODE) Learn(now);

        if (!introShown) { introShown = true; introUntil = now + 7000; }
        if (now < introUntil && !(LEARN_MODE && now < learnUntil))
            GTA.UI.Screen.ShowSubtitle("SurvivalMoves ready: hold Aim + Cover = bandage, hold Aim + Weapon = armor, Sprint + Stealth = slide", 100);

        if (p.IsInVehicle()) { sliding = false; return; }

        // กันปุ่ม D-pad ทำงานอย่างอื่น (เช่น เปิดโทรศัพท์)
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)BANDAGE_BTN, true);
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)ARMOR_BTN, true);

        // ---------- คอมโบกับปุ่มของพอร์ต ----------
        bool comboOk = !ONLY_WHEN_UNARMED || p.Weapons.Current.Hash == WeaponHash.Unarmed;
        bool mod = comboOk && AnyHeld(MOD_IDS);
        if (mod)
        {
            DisableAll(BANDAGE_IDS);   // กัน Cover / วงล้ออาวุธ ทำงานซ้อน
            DisableAll(ARMOR_IDS);
        }
        bool sprinting = p.IsSprinting;
        if (sprinting) DisableAll(SLIDE_IDS);   // กัน Stealth สลับโหมดตอนวิ่ง

        bool bDown = (GetAsyncKeyState(BANDAGE_KEY) & 0x8000) != 0;
        bool aDown = (GetAsyncKeyState(ARMOR_KEY) & 0x8000) != 0;
        bool bandagePressed = Pressed(BANDAGE_BTN) || (bDown && !prevBandageKey);
        bool armorPressed = Pressed(ARMOR_BTN) || (aDown && !prevArmorKey);
        prevBandageKey = bDown; prevArmorKey = aDown;

        if (mod && AnyJust(BANDAGE_IDS)) bandagePressed = true;
        if (mod && AnyJust(ARMOR_IDS)) StartArmor(p, now);
        if (sprinting && AnyJust(SLIDE_IDS)) StartSlide(p, now);

        // ---------- พันแผล ----------
        if (bandagePressed) StartBandage(p, now);
        if (bandaging)
        {
            if (now >= nextHeal && healLeft > 0)
            {
                p.Health = Math.Min(p.MaxHealth, p.Health + BANDAGE_STEP);
                healLeft -= BANDAGE_STEP;
                nextHeal = now + BANDAGE_TICK_MS;
            }
            if (healLeft <= 0 || now >= bandageEnd || p.Health >= p.MaxHealth)
            {
                bandaging = false;
                StopAnim(p, BANDAGE_DICT, BANDAGE_ANIM);
            }
        }

        // ---------- ใส่เกราะ / สไลด์ (D-pad ขวา) ----------
        if (armorPressed)
        {
            if (sprinting)
            {
                if (lastTap > 0 && now - lastTap < DOUBLE_TAP_MS)
                {
                    pendingArmor = false;
                    lastTap = 0;
                    StartSlide(p, now);
                }
                else
                {
                    lastTap = now;
                    pendingArmor = true;
                }
            }
            else StartArmor(p, now);
        }
        if (pendingArmor && now - lastTap > DOUBLE_TAP_MS)
        {
            pendingArmor = false;
            lastTap = 0;
            if (!sliding) StartArmor(p, now);
        }
        if (armoring && now >= armorEnd)
        {
            armoring = false;
            p.Armor = 100;
            StopAnim(p, ARMOR_DICT, ARMOR_ANIM);
            GTA.UI.Screen.ShowSubtitle("Armor on");
        }

        // ตัวถึกขึ้นเมื่อมีเกราะ: ไม่โดนฮิตคริติคอล และล้มยากขึ้น
        bool armored = p.Armor > 0;
        Function.Call(Hash.SET_PED_SUFFERS_CRITICAL_HITS, p, !armored);
        Function.Call(Hash.SET_PED_CAN_RAGDOLL_FROM_PLAYER_IMPACT, p, !armored);

        // ---------- สไลด์ ----------
        if (sliding)
        {
            int t = now - slideStart;
            if (t >= SLIDE_MS)
            {
                sliding = false;
                StopAnim(p, SLIDE_DICT, SLIDE_ANIM);
            }
            else
            {
                float speed = SLIDE_SPEED * (1f - (float)t / SLIDE_MS) + 1.5f;
                Vector3 v = slideDir * speed;
                v.Z = p.Velocity.Z;
                p.Velocity = v;
            }
        }
    }

    void StartBandage(Ped p, int now)
    {
        if (bandaging || sliding) return;
        if (p.Health >= p.MaxHealth) { GTA.UI.Screen.ShowSubtitle("Health is already full"); return; }
        bandaging = true;
        healLeft = BANDAGE_TOTAL;
        nextHeal = now + BANDAGE_TICK_MS;
        bandageEnd = now + (BANDAGE_TOTAL / BANDAGE_STEP) * BANDAGE_TICK_MS;
        PlayAnim(p, BANDAGE_DICT, BANDAGE_ANIM, bandageEnd - now, 49);
    }

    void StartArmor(Ped p, int now)
    {
        if (armoring || sliding) return;
        if (p.Armor >= 100) { GTA.UI.Screen.ShowSubtitle("Armor is already full"); return; }
        armoring = true;
        armorEnd = now + ARMOR_ANIM_MS;
        PlayAnim(p, ARMOR_DICT, ARMOR_ANIM, ARMOR_ANIM_MS, 49);
    }

    void StartSlide(Ped p, int now)
    {
        if (sliding) return;
        if (armoring) { armoring = false; StopAnim(p, ARMOR_DICT, ARMOR_ANIM); }
        if (bandaging) { bandaging = false; StopAnim(p, BANDAGE_DICT, BANDAGE_ANIM); }
        sliding = true;
        slideStart = now;
        slideDir = p.ForwardVector;
        PlayAnim(p, SLIDE_DICT, SLIDE_ANIM, SLIDE_MS, 0);
    }
}
