using System;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using Control = GTA.Control;

// SurvivalMoves - ScriptHookVDotNet v3
// D-pad ซ้าย (จอย) / F6 (คีย์บอร์ด) = พันแผล  (เลือดค่อยๆ เพิ่ม)
// D-pad ขวา (จอย) / F7 (คีย์บอร์ด) = ใส่เกราะ (เกราะ 100 + ตัวถึกขึ้น)
// วิ่งอยู่แล้วกดปุ่มใส่เกราะรัว 2 ครั้ง = สไลด์พุ่งไปข้างหน้า
public class SurvivalMoves : Script
{
    // ---------- ตั้งค่าปุ่ม / ค่าต่างๆ ----------
    const Control BANDAGE_BTN = Control.PhoneLeft;   // D-pad ซ้าย
    const Control ARMOR_BTN   = Control.PhoneRight;  // D-pad ขวา
    const Keys BANDAGE_KEY = Keys.F6;
    const Keys ARMOR_KEY   = Keys.F7;

    const int   DOUBLE_TAP_MS   = 600;   // กดรัวภายในกี่ ms ถึงนับเป็นสไลด์
    const int   BANDAGE_TOTAL   = 60;    // เลือดที่ได้รวม
    const int   BANDAGE_STEP    = 10;    // เพิ่มทีละเท่าไหร่
    const int   BANDAGE_TICK_MS = 500;   // เพิ่มทุกกี่ ms
    const int   ARMOR_ANIM_MS   = 1800;  // เวลาท่าใส่เกราะ
    const int   SLIDE_MS        = 900;   // ระยะเวลาสไลด์
    const float SLIDE_SPEED     = 8f;    // ความเร็วเริ่มต้นตอนสไลด์

    // ท่าทาง (ถ้าไม่ถูกใจ เปลี่ยนชื่อได้)
    const string BANDAGE_DICT = "amb@medic@standing@tendtodead@base";
    const string BANDAGE_ANIM = "base";
    const string ARMOR_DICT   = "clothingtie";
    const string ARMOR_ANIM   = "try_tie_positive_a";
    const string SLIDE_DICT   = "move_jump";
    const string SLIDE_ANIM   = "dive_start_run";
    // -------------------------------------------

    bool kbBandage, kbArmor;

    bool bandaging; int healLeft; int nextHeal; int bandageEnd;
    bool armoring; int armorEnd;
    bool pendingArmor; int lastTap;
    bool sliding; int slideStart; Vector3 slideDir;

    public SurvivalMoves()
    {
        Tick += OnTick;
        KeyDown += (s, e) =>
        {
            if (e.KeyCode == BANDAGE_KEY) kbBandage = true;
            if (e.KeyCode == ARMOR_KEY) kbArmor = true;
        };
    }

    bool Pressed(Control c)
    {
        return Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)c);
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

    void OnTick(object sender, EventArgs e)
    {
        Ped p = Game.Player.Character;
        if (p == null || !p.Exists() || p.IsDead || p.IsInVehicle()) { sliding = false; return; }

        // กันปุ่ม D-pad ทำงานอย่างอื่น (เช่น เปิดโทรศัพท์)
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)BANDAGE_BTN, true);
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)ARMOR_BTN, true);

        bool bandagePressed = Pressed(BANDAGE_BTN) || kbBandage;
        bool armorPressed = Pressed(ARMOR_BTN) || kbArmor;
        kbBandage = false; kbArmor = false;
        int now = Game.GameTime;

        // ---------- พันแผล ----------
        if (bandagePressed && !bandaging && !sliding)
        {
            if (p.Health >= p.MaxHealth)
                GTA.UI.Screen.ShowSubtitle("เลือดเต็มอยู่แล้ว");
            else
            {
                bandaging = true;
                healLeft = BANDAGE_TOTAL;
                nextHeal = now + BANDAGE_TICK_MS;
                bandageEnd = now + (BANDAGE_TOTAL / BANDAGE_STEP) * BANDAGE_TICK_MS;
                PlayAnim(p, BANDAGE_DICT, BANDAGE_ANIM, bandageEnd - now, 49);
            }
        }
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

        // ---------- ใส่เกราะ / สไลด์ ----------
        if (armorPressed)
        {
            if (p.IsSprinting)
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
                    pendingArmor = true;   // รอดูว่าจะกดซ้ำเป็นสไลด์ไหม
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
            GTA.UI.Screen.ShowSubtitle("ใส่เกราะแล้ว");
        }

        // ตัวถึกขึ้นเมื่อมีเกราะ: ไม่โดนฮิตคริติคอล (เฮดช็อตตายทันที) และล้มยากขึ้น
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

    void StartArmor(Ped p, int now)
    {
        if (armoring || sliding) return;
        if (p.Armor >= 100) { GTA.UI.Screen.ShowSubtitle("เกราะเต็มอยู่แล้ว"); return; }
        armoring = true;
        armorEnd = now + ARMOR_ANIM_MS;
        PlayAnim(p, ARMOR_DICT, ARMOR_ANIM, ARMOR_ANIM_MS, 49);
    }

    void StartSlide(Ped p, int now)
    {
        if (sliding) return;
        // ยกเลิกท่าอื่นที่ค้างอยู่
        if (armoring) { armoring = false; StopAnim(p, ARMOR_DICT, ARMOR_ANIM); }
        if (bandaging) { bandaging = false; StopAnim(p, BANDAGE_DICT, BANDAGE_ANIM); }
        sliding = true;
        slideStart = now;
        slideDir = p.ForwardVector;
        PlayAnim(p, SLIDE_DICT, SLIDE_ANIM, SLIDE_MS, 0);
    }
}
