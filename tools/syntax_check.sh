#!/bin/bash
# 独立类型检查：用 Unity 自带 Roslyn 编译全部 Assets/Scripts 源码（项目被编辑器占用时的代替编译验证）
set -e
cd "$(dirname "$0")/.."
UNITY="D:\\Unity\\2022.3.57f1c2\\Editor\\Data"
DOTNET="$UNITY\\NetCoreRuntime\\dotnet.exe"
CSC="$UNITY\\DotNetSdkRoslyn\\csc.dll"
MANAGED="$UNITY\\Managed\\UnityEngine"

REFS=(
  -r:"$UNITY\\NetStandard\\ref\\2.1.0\\netstandard.dll"
  -r:"Library/ScriptAssemblies/UnityEngine.UI.dll"
  -r:"$MANAGED\\UnityEngine.CoreModule.dll"
  -r:"$MANAGED\\UnityEngine.JSONSerializeModule.dll"
  -r:"$MANAGED\\UnityEngine.InputLegacyModule.dll"
  -r:"$MANAGED\\UnityEngine.ImageConversionModule.dll"
  -r:"$MANAGED\\UnityEngine.UIModule.dll"
  -r:"$MANAGED\\UnityEngine.IMGUIModule.dll"
  -r:"$MANAGED\\UnityEngine.TextRenderingModule.dll"
  -r:"$MANAGED\\UnityEngine.AudioModule.dll"
  -r:"$MANAGED\\UnityEngine.VideoModule.dll"
  -r:"$MANAGED\\UnityEngine.PhysicsModule.dll"
  -r:"$MANAGED\\UnityEngine.ParticleSystemModule.dll"
  -r:"$MANAGED\\UnityEngine.AnimationModule.dll"
  -r:"$MANAGED\\UnityEngine.UnityWebRequestModule.dll"
  -r:"$MANAGED\\UnityEngine.UnityWebRequestAudioModule.dll"
  -r:"$MANAGED\\UnityEngine.AndroidJNIModule.dll"
  -r:"Library/ScriptAssemblies/Unity.TextMeshPro.dll"
)

# 运行时程序集（Assets/Scripts）
FAIL=0
MAPFILE=(-out:"$TEMP/mg_syntaxcheck.dll" -nowarn:CS0169,CS0649,CS0414)
while IFS= read -r f; do
  MAPFILE+=("$f")
done < <(find Assets/Scripts -name '*.cs')

"$DOTNET" "$CSC" -nologo -target:library "${MAPFILE[@]}" "${REFS[@]}" 2>&1 | head -40
rc=${PIPESTATUS[0]}; [ "$rc" -ne 0 ] && FAIL=1

# 编辑器程序集（Assets/Editor）：SafeMode 事故（CS0152 重复 case）证明它必须单独查
EREFS=(
  -r:"$UNITY\\NetStandard\\ref\\2.1.0\\netstandard.dll"
  -r:"$UNITY\\Managed\\UnityEditor.dll"
  -r:"$MANAGED\\UnityEngine.CoreModule.dll"
  -r:"$MANAGED\\UnityEngine.ScreenCaptureModule.dll"
  -r:"$MANAGED\\UnityEngine.IMGUIModule.dll"
  -r:"$MANAGED\\UnityEngine.UIModule.dll"
  -r:"$MANAGED\\UnityEngine.AudioModule.dll"
  -r:"$MANAGED\\UnityEngine.VideoModule.dll"
  -r:"$MANAGED\\UnityEngine.TextRenderingModule.dll"
  -r:"$MANAGED\\UnityEngine.JSONSerializeModule.dll"
  -r:"$MANAGED\\UnityEngine.dll"
  -r:"Library/ScriptAssemblies/Assembly-CSharp.dll"
  -r:"Library/ScriptAssemblies/UnityEngine.UI.dll"
  -r:"Library/ScriptAssemblies/Unity.TextMeshPro.dll"
)
EMAP=(-out:"$TEMP/mg_syntaxcheck_editor.dll" -nowarn:CS0169,CS0649,CS0414)
while IFS= read -r f; do
  EMAP+=("$f")
done < <(find Assets/Editor -name '*.cs')

"$DOTNET" "$CSC" -nologo -target:library "${EMAP[@]}" "${EREFS[@]}" 2>&1 | head -40
rc=${PIPESTATUS[0]}; [ "$rc" -ne 0 ] && FAIL=1
exit $FAIL
