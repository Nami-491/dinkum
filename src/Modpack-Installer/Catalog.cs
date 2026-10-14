using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace MakeshiftSetup
{
    internal sealed class Mod
    {
        internal string Key, Name, Guid, File, Description;
        internal bool DefaultSelected => Key != "recovery";
        internal string Target => "BepInEx/plugins/" + File;
        internal string Config => "BepInEx/config/" + Guid + ".cfg";
        public override string ToString() => Name;
    }
    internal static class Catalog
    {
        internal static readonly Mod[] Mods = {
            new Mod { Key="makeshift", Name="Makeshift 1.0.3 · 상자 재료로 제작", Guid="Snaitf.Dinkum.Makeshift", File="Snaitf.Dinkum.Makeshift.dll", Description="방장 + 기능 사용자 설치. 본섬 일반 상자 재료를 제작·요리에 사용합니다. 동시 제작의 완전한 트랜잭션 처리는 미구현입니다." },
            new Mod { Key="fancy", Name="Fancy Feast 1.0.6 · NPC 선호 음식 표시", Guid="Snaitf.Dinkum.FancyFeast", File="Snaitf.Dinkum.FancyFeast.dll", Description="개인 화면용. 사용할 사람만 설치하면 됩니다. 현재 게임의 비공개 UI 필드와 NPC 범위 검사를 반영한 수정판입니다." },
            new Mod { Key="inventory", Name="Inventory Management 0.12.0 · 상자 보내기 / 개별 아이템 잠금", Guid="dev.TinyResort.InventoryManagement", File="InventoryManagement.dll", Description="방장 + 참가자 설치. F6 정렬 / F7 상자 보내기 / 아이템을 가리키고 F8 잠금. 같은 종류가 여러 개여도 선택한 복사본/묶음만 보호하며 수동 인벤토리 이동 시 따라갑니다. 기존 칸 잠금은 재적용하지 않으므로 한 번 다시 지정하세요. 키 설정은 보존합니다." },
            new Mod { Key="fish", Name="Fish In A Barrel 1.0.2 · 생물 겹치기/상자 보관", Guid="Snaitf.Dinkum.FishInABarrel", File="Snaitf.Dinkum.FishInABarrel.dll", Description="함께 플레이하는 전원의 동일 적용을 권장합니다. 제거/초기 복원 전 생물 묶음을 반드시 1마리씩 분리하세요. 세이브 아이템을 자동 변환하지 않습니다." },
            new Mod { Key="prices", Name="Sale Prices 1.0.0 · 기본 판매가격 표시", Guid="LocalDinkum.SalePrices", File="Snaitf.Dinkum.SalePrices.dll", Description="개인 표시 기능. 아이템 설명에 개당·묶음 기본 판매가 표시. 상업 면허·판매처·행사 보너스 제외. 인벤토리에서 F10 켜기/끄기. 단축키 변경 가능." },
            new Mod { Key="npc", Name="NPC Map 1.0.0 · 멀티 NPC 위치 지도", Guid="LocalDinkum.NpcMap", File="Snaitf.Dinkum.NpcMap.dll", Description="방장과 사용자 모두 설치. 본섬 전체 지도에서 NPC 위치를 2초 간격으로 조회. 실내 NPC는 건물 입구, 6초 이상 오래된 정보는 숨김. 지도에서 F9 켜기/끄기. 지하·다른 섬 미지원." },
            new Mod { Key="enhancement", Name="장비 강화 1.2.0 · +20 / 전원 설치 필수", Guid="LocalDinkum.EquipmentEnhancement", File="LocalDinkum.EquipmentEnhancement.dll", Description="방장과 참가자 전원 동일 버전 필수. F11 강화. +10까지 실패 유지, 현재 +11 이상은 실패 시 1단계 하락(+10 하한), 파괴 없음. 성공·실패 모두 비용 소모. 게임 첫 화면의 참가자 세이브 호환성 설정에서 구 저장을 백업·변환하세요. 제거·초기 복원은 별도의 강화 모드 제거 준비가 필요합니다." },
            new Mod { Key="recovery", Name="강화 장비 찾기 1.2.0 · 방장 전용 / 참가자는 불필요", Guid="LocalDinkum.EquipmentRecovery", File="LocalDinkum.EquipmentRecovery.dll", Description="방장만 선택. 장비 강화 1.2.0 필요. F5 검색 / Shift+F5 다음 / Ctrl+F5 두 번으로 선택 장비를 발밑 이동 / Alt+F5 닫기. +20까지 현재 로드된 강화 드롭 검색, 같은 구역에서 회수. 다른 사람 장비도 포함될 수 있습니다. 실제 원격 20인 검증은 아직 없습니다." },
        };
        internal static Mod[] Select(string[] keys)
        {
            if (keys.Any(k => !Mods.Any(m => m.Key == k))) throw new ArgumentException("알 수 없는 모드 선택입니다.");
            return Mods.Where(m => keys.Contains(m.Key)).ToArray();
        }
    }
    internal static partial class Engine
    {
        private static bool Matches(string path, Mod mod)
        {
            if (Path.GetFileName(path).Equals(mod.File, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                using (var module = ModuleDefinition.ReadModule(path))
                    return module.Types.SelectMany(t => t.CustomAttributes).Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin" && a.ConstructorArguments.Count > 0 && (string)a.ConstructorArguments[0].Value == mod.Guid);
            }
            catch (BadImageFormatException) { return false; }
        }
    }
}
