using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
#if !LEGACY
using BepInEx.Unity.Mono;
using KeyboardShortcut = BepInEx.Unity.Mono.Configuration.KeyboardShortcut;
#endif
using LocalDinkum.Enhancement;
using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;

namespace LocalDinkumMods
{
    // Explicit-cost enhancement UI. No attempt is initiated merely by opening it.
    [BepInPlugin("LocalDinkum.EquipmentEnhancement", "Equipment Enhancement", "1.2.0")]
    public sealed class EquipmentEnhancement : BaseUnityPlugin
    {
        private ConfigEntry<KeyboardShortcut> openKey;
        private GameObject window;
        private Inventory owner;
        private InventorySlot selected;
        private int selectedId, previewLevel;
        private TextMeshProUGUI title, stats, cost, warning;
        private float refreshAt, logAt;
        private NetworkConnection connection;
        internal static bool ModalOpen;
        private Button attempt, cancel;
        private bool cursorCaptured, previousCursorVisible;
        private CursorLockMode previousCursorLock;
        private CanvasGroup hiddenCursor, hiddenSnapCursor;
        private bool addedCursorGroup, addedSnapGroup;
        private float previousCursorAlpha, previousSnapAlpha;
        private void Awake()
        {
            openKey = Config.Bind("Keybinds", "OpenEnhancement", new KeyboardShortcut(KeyCode.F11), "Hover equipment and open the enhancement window. The attempt button consumes only Dinks on success AND failure.");
            var harmony = new Harmony("LocalDinkum.EquipmentEnhancement");
            InstallPatches(harmony, typeof(EnhancementCompatibility));
            try { EnhancementRegistry.CheckGame(); }
            catch (Exception e) { EnhancementCompatibility.Error = e.Message; Logger.LogError(e); return; }
            EnhancementNetwork.Setup();
            InstallPatches(harmony, typeof(EnhancementRegistry));
            InstallPatches(harmony, typeof(EnhancementNetwork));
            InstallPatches(harmony, typeof(EnhancementTransactions));
            Logger.LogInfo("Equipment Enhancement 1.2.0: +20; failure above +10 loses one tier, floor +10; no destruction. Identical peer registry required.");
        }
        private static void InstallPatches(Harmony harmony, Type container)
        {
            // Each helper method names its own original. Explicit installation
            // avoids ambiguous multi-target class-processor annotation merging.
            foreach (var method in container.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic))
            {
                var annotation = (HarmonyPatch)Attribute.GetCustomAttribute(method, typeof(HarmonyPatch));
                if (annotation == null) continue;
                var spec = annotation.info;
                var original = AccessTools.Method(spec.declaringType, spec.methodName, spec.argumentTypes);
                if (original == null) throw new MissingMethodException(spec.declaringType.FullName, spec.methodName);
                bool prefix = Attribute.IsDefined(method, typeof(HarmonyPrefix));
                var patch = new HarmonyMethod(method);
                harmony.Patch(original, prefix ? patch : null, prefix ? null : patch);
            }
        }
        private bool confirmConversion;
        private void OnGUI()
        {
            if (EnhancementSaveUpgrade.Draw()) return;
            if (!string.IsNullOrEmpty(EnhancementCompatibility.Error))
            {
                GUI.Box(new Rect(20, 20, Math.Min(900, Screen.width - 40), 100), "Equipment compatibility: load/save blocked.\n" + EnhancementCompatibility.Error);
                return;
            }
            if ((!EnhancementConversion.CanConvert() && !EnhancementConversion.Completed) || !Inventory.Instance) return;
            var saved = GUI.skin.font;
            try
            {
                if (Inventory.Instance.InvDescriptionText && Inventory.Instance.InvDescriptionText.font && Inventory.Instance.InvDescriptionText.font.sourceFontFile) GUI.skin.font = Inventory.Instance.InvDescriptionText.font.sourceFontFile;
                if (GUI.Button(new Rect(Screen.width - 290, 20, 270, 45), "강화 모드 제거 준비")) confirmConversion = !confirmConversion;
                if (!confirmConversion) return;
                GUI.Box(new Rect(Screen.width / 2 - 310, 90, 620, 250), "모든 로컬 캐릭터/섬의 강화 장비를 일반 장비로 변환합니다.");
                GUI.Label(new Rect(Screen.width / 2 - 290, 130, 580, 90), "장비는 남고 강화 수치는 제거됩니다. 원본은 별도로 백업합니다.\n다른 참가자는 각자 자신의 PC에서 변환해야 합니다.\n변환 뒤 게임을 종료하고 설치기에서 초기 복원을 실행하세요.");
                if (!EnhancementConversion.Completed && GUI.Button(new Rect(Screen.width / 2 - 270, 240, 250, 45), "확인 · 전체 저장 파일 변환"))
                    try { EnhancementConversion.ConvertAll(); } catch (Exception e) { EnhancementConversion.Status = "변환 중단: " + e.Message; Logger.LogError(e); }
                if (GUI.Button(new Rect(Screen.width / 2 + 20, 240, 250, 45), "닫기")) confirmConversion = false;
                GUI.Label(new Rect(Screen.width / 2 - 300, 350, 600, 160), EnhancementConversion.Status);
            }
            finally { GUI.skin.font = saved; }
        }
        private void Update()
        {
            try
            {
                EnhancementNetwork.Tick(); EnhancementTransactions.Tick();
                EnhancementSelfTest.Tick();
                EnhancementTransactions.SetModal(ModalOpen || EnhancementTransactions.Pending);
                if (window)
                {
                    if (selected && !EnhancementTransactions.Pending) selectedId = selected.itemNo;
                    if (!owner || owner != Inventory.Instance || !owner.invOpen || !NetworkClient.isConnected || connection != NetworkClient.connection || !selected || selected.itemNo != selectedId ||
                        (SaveLoad.saveOrLoad && SaveLoad.saveOrLoad.isSaving) || (ChatBox.chat && ChatBox.chat.chatOpen) || Input.GetKeyDown(KeyCode.Escape) || openKey.Value.IsDown())
                    { if (!EnhancementTransactions.Pending || !NetworkClient.isConnected) Close(); return; }
                    if (Time.realtimeSinceStartup >= refreshAt) { refreshAt = Time.realtimeSinceStartup + .25f; Refresh(); }
                    return;
                }
                if (!openKey.Value.IsDown() || !Ready()) return;
                var slot = Inventory.Instance.cursorRollOver();
                if (!slot || !Inventory.Instance.invSlots.Contains(slot) || !slot.slotUnlocked || !Common.Item(slot.itemNo) || !Supported(Inventory.Instance.allItems[slot.itemNo]))
                { Common.Notify("인벤토리의 내구도가 있는 근접 무기·도끼·곡괭이를 가리켜 주세요."); return; }
                EnhancementTransactions.Recover();
                selected = slot; selectedId = slot.itemNo; previewLevel = EnhancementRegistry.Describe(selectedId).Level;
                owner = Inventory.Instance; connection = NetworkClient.connection;
                CreateWindow(); Refresh();
            }
            catch (Exception e)
            {
                Close();
                    if (Time.realtimeSinceStartup >= logAt) { logAt = Time.realtimeSinceStartup + 10; Logger.LogError("Enhancement window closed: " + e); }
            }
        }
        private static bool Ready() => !EnhancementTransactions.TransferPending && !EnhancementConversion.Completed && Inventory.Instance && Inventory.Instance.invOpen && !Inventory.Instance.menuOpen && NetworkClient.isConnected && NetworkClient.ready &&
            NetworkMapSharer.Instance && NetworkMapSharer.Instance.localChar &&
            !(SaveLoad.saveOrLoad && SaveLoad.saveOrLoad.isSaving) && !(ChatBox.chat && ChatBox.chat.chatOpen) &&
            !(CraftingManager.manage && CraftingManager.manage.craftMenuOpen) && !(ChestWindow.chests && ChestWindow.chests.chestWindowOpen) &&
            Inventory.Instance.dragSlot && Inventory.Instance.dragSlot.itemNo == -1 && Inventory.Instance.weaponSlot && Inventory.Instance.weaponSlot.itemNo == -1;
        private static bool HarvestTool(InventoryItem item) => item.myAnimType == InventoryItem.typeOfAnimation.Axe || item.myAnimType == InventoryItem.typeOfAnimation.Pickaxe;
        private static bool Weapon(InventoryItem item) => item.itemPrefab && item.itemPrefab.GetComponent<MeleeAttacks>() && item.itemPrefab.GetComponent<MeleeAttacks>().isWeapon;
        private static bool Supported(InventoryItem item) => item.hasFuel && item.fuelMax > 0 && !item.hasColourVariation && !item.isPowerTool && (HarvestTool(item) || Weapon(item));
        private void Refresh()
        {
            if (!Common.Item(selectedId)) { Close(); return; }
            var item = owner.allItems[selectedId];
            previewLevel = EnhancementRegistry.Describe(selectedId).Level;
            int target = Math.Min(EnhancementRules.MaximumLevel, previewLevel + 1);
            var quote = EnhancementRules.CostForTarget(target);
            string name = item.getInvItemName();
            title.text = "장비 강화\n" + name + (previewLevel == EnhancementRules.MaximumLevel ? " · 최대 강화" : " → +" + target);
            string power = HarvestTool(item) ? "채집 타격 효율" : "공격력";
            stats.text = power + ": 기본 대비 " + EnhancementRules.AttackOrHarvestPercent(previewLevel) + "% → " + EnhancementRules.AttackOrHarvestPercent(target) + "%\n" +
                "최대 내구도: " + item.fuelMax.ToString("N0") + " → " + EnhancementRules.Capacity(owner.allItems[EnhancementRegistry.Describe(selectedId).BaseId].fuelMax, target).ToString("N0") + "\n" +
                "성공 확률: " + (quote.SuccessBasisPoints / 100) + "% · 실패 시 " + (previewLevel > 10 ? "+" + (previewLevel - 1) + " 하락" : "단계 유지") + " / 파괴 없음";
            cost.text = "시도 비용 (성공·실패 공통)\n" +
                "딩크만 사용 · 주괴 등 아이템 재료 불필요\n" +
                quote.Dinks.ToString("N0") + " Dinks / 보유 " + owner.wallet.ToString("N0") + " Dinks";
            warning.text = "+11 이상 장비는 실패 시 1단계 하락합니다(+10 하한). 파괴는 없습니다.\n성공·실패 모두 비용 소모. " + EnhancementTransactions.Status;
            if (previewLevel == EnhancementRules.MaximumLevel)
            {
                stats.text = power + ": 기본 대비 " + EnhancementRules.AttackOrHarvestPercent(previewLevel) + "%\n최대 내구도: " + item.fuelMax.ToString("N0") + "\n최대 강화 단계입니다.";
                cost.text = "추가 강화 불가 · 비용 없음";
            }
            if (attempt) attempt.interactable = previewLevel < EnhancementRules.MaximumLevel && !EnhancementTransactions.Pending && !EnhancementTransactions.Fatal && EnhancementNetwork.Accepted;
            if (cancel) cancel.interactable = EnhancementTransactions.Pending && !EnhancementTransactions.Fatal;
        }
        private void CreateWindow()
        {
            ModalOpen = true;
            CaptureCursor();
            window = new GameObject("LocalEnhancementWindow", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = window.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 32760;
            var scaler = window.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1280, 720); scaler.matchWidthOrHeight = .5f;
            var shield = new GameObject("InputShield", typeof(RectTransform), typeof(Image)); shield.transform.SetParent(window.transform, false);
            var rect = shield.GetComponent<RectTransform>(); rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
            shield.GetComponent<Image>().color = new Color(0, 0, 0, .65f);
            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image)); panel.transform.SetParent(shield.transform, false);
            rect = panel.GetComponent<RectTransform>(); rect.sizeDelta = new Vector2(720, 590); rect.anchoredPosition = Vector2.zero;
            panel.GetComponent<Image>().color = new Color(.08f, .12f, .16f, 1);
            title = Label(panel.transform, 24, 26, 672, 75, 25);
            stats = Label(panel.transform, 24, 112, 672, 108, 21);
            cost = Label(panel.transform, 24, 238, 672, 100, 20);
            warning = Label(panel.transform, 24, 350, 672, 90, 18); warning.color = new Color(1, .8f, .4f);
            AddButton(panel.transform, "닫기 (Esc)", 464, 454, 230, () => { if (!EnhancementTransactions.Pending) Close(); });
            cancel = AddButton(panel.transform, "대기 요청 취소 (확정 전·비용 없음)", 24, 454, 425, () => { EnhancementTransactions.Cancel(); Refresh(); });
            attempt = AddButton(panel.transform, "강화 시도 · 딩크 소모", 24, 518, 670, () => { try { EnhancementTransactions.Begin(selected); } catch (Exception e) { EnhancementTransactions.Status = e.Message; } Refresh(); });
        }
        private TextMeshProUGUI Label(Transform parent, float x, float y, float width, float height, float size)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI)); go.transform.SetParent(parent, false);
            Place(go.GetComponent<RectTransform>(), x, y, width, height);
            var text = go.GetComponent<TextMeshProUGUI>(); text.fontSize = size; text.color = Color.white; text.raycastTarget = false; text.richText = false;
            if (owner && owner.InvDescriptionText) text.font = owner.InvDescriptionText.font;
            text.textWrappingMode = TextWrappingModes.Normal; text.overflowMode = TextOverflowModes.Ellipsis;
            return text;
        }
        private Button AddButton(Transform parent, string label, float x, float y, float width, Action action)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button)); go.transform.SetParent(parent, false);
            Place(go.GetComponent<RectTransform>(), x, y, width, 46);
            var background = go.GetComponent<Image>(); background.color = new Color(.22f, .35f, .4f);
            var button = go.GetComponent<Button>(); button.targetGraphic = background;
            if (action != null) button.onClick.AddListener(() => action());
            var text = Label(go.transform, 8, 5, width - 16, 36, 18); text.text = label; text.alignment = TextAlignmentOptions.Center;
            return button;
        }
        private static void Place(RectTransform rect, float x, float y, float width, float height)
        { rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height); }
        private void CaptureCursor()
        {
            if (cursorCaptured) return;
            previousCursorVisible = Cursor.visible; previousCursorLock = Cursor.lockState; cursorCaptured = true;
            HideCursor(owner ? owner.cursor : null, out hiddenCursor, out addedCursorGroup, out previousCursorAlpha);
            HideCursor(owner ? owner.snappingCursor : null, out hiddenSnapCursor, out addedSnapGroup, out previousSnapAlpha);
            MaintainCursor();
        }
        private static void HideCursor(Transform target, out CanvasGroup group, out bool added, out float alpha)
        {
            group = target ? target.GetComponent<CanvasGroup>() : null; added = target && !group; alpha = group ? group.alpha : 1f;
            if (added) group = target.gameObject.AddComponent<CanvasGroup>();
            if (group) group.alpha = 0f;
        }
        private void LateUpdate() { if (cursorCaptured) MaintainCursor(); }
        private void MaintainCursor()
        {
            // The game's cursor is a UI image below this modal. Hardware cursor
            // stays above every canvas and follows the same physical pointer as
            // Unity's Button input; avoid moving/reparenting the native cursor.
            Cursor.lockState = CursorLockMode.Confined; Cursor.visible = true;
            if (hiddenCursor) hiddenCursor.alpha = 0f;
            if (hiddenSnapCursor) hiddenSnapCursor.alpha = 0f;
        }
        private void RestoreCursor()
        {
            if (!cursorCaptured) return;
            cursorCaptured = false;
            if (hiddenCursor) { hiddenCursor.alpha = previousCursorAlpha; if (addedCursorGroup) Destroy(hiddenCursor); }
            if (hiddenSnapCursor) { hiddenSnapCursor.alpha = previousSnapAlpha; if (addedSnapGroup) Destroy(hiddenSnapCursor); }
            hiddenCursor = hiddenSnapCursor = null;
            Cursor.lockState = previousCursorLock; Cursor.visible = previousCursorVisible;
        }
        private void Close() { if (window) { window.SetActive(false); Destroy(window); } RestoreCursor(); ModalOpen = false; window = null; selected = null; owner = null; connection = null; EnhancementTransactions.SetModal(EnhancementTransactions.Pending); }
        private void OnDisable() => Close();
        private void OnDestroy() => Close();
    }
}
