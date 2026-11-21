using UnityCSharpFastLSP.Protocol;

namespace UnityCSharpFastLSP.Semantics;

public static class UnityApiDictionary
{
    private static readonly Dictionary<string, List<Symbol>> _unityTypes = new(StringComparer.OrdinalIgnoreCase);

    static UnityApiDictionary()
    {
        // Transform
        RegisterType("Transform",
            Field("position", "Vector3 position"),
            Field("localPosition", "Vector3 localPosition"),
            Field("rotation", "Quaternion rotation"),
            Field("localRotation", "Quaternion localRotation"),
            Field("eulerAngles", "Vector3 eulerAngles"),
            Field("localEulerAngles", "Vector3 localEulerAngles"),
            Field("localScale", "Vector3 localScale"),
            Field("parent", "Transform parent"),
            Field("root", "Transform root"),
            Field("childCount", "int childCount"),
            Field("forward", "Vector3 forward"),
            Field("up", "Vector3 up"),
            Field("right", "Vector3 right"),
            Field("gameObject", "GameObject gameObject"),
            Method("Translate", "void Translate(Vector3 translation)"),
            Method("Rotate", "void Rotate(Vector3 eulers)"),
            Method("LookAt", "void LookAt(Transform target)"),
            Method("Find", "Transform Find(string n)"),
            Method("GetChild", "Transform GetChild(int index)"),
            Method("SetParent", "void SetParent(Transform parent)")
        );

        // GameObject
        RegisterType("GameObject",
            Field("transform", "Transform transform"),
            Field("layer", "int layer"),
            Field("tag", "string tag"),
            Field("activeSelf", "bool activeSelf"),
            Field("activeInHierarchy", "bool activeInHierarchy"),
            Field("name", "string name"),
            Method("GetComponent", "T GetComponent<T>()"),
            Method("GetComponents", "T[] GetComponents<T>()"),
            Method("AddComponent", "T AddComponent<T>()"),
            Method("SetActive", "void SetActive(bool value)"),
            Method("CompareTag", "bool CompareTag(string tag)"),
            Method("Instantiate", "GameObject Instantiate(GameObject original)"),
            Method("Destroy", "void Destroy(Object obj)")
        );

        // Vector3
        RegisterType("Vector3",
            Field("x", "float x"),
            Field("y", "float y"),
            Field("z", "float z"),
            Field("normalized", "Vector3 normalized"),
            Field("magnitude", "float magnitude"),
            Field("sqrMagnitude", "float sqrMagnitude"),
            Field("zero", "Vector3 zero"),
            Field("one", "Vector3 one"),
            Field("up", "Vector3 up"),
            Field("down", "Vector3 down"),
            Field("left", "Vector3 left"),
            Field("right", "Vector3 right"),
            Field("forward", "Vector3 forward"),
            Field("back", "Vector3 back"),
            Method("Normalize", "void Normalize()"),
            Method("Distance", "float Distance(Vector3 a, Vector3 b)"),
            Method("Dot", "float Dot(Vector3 lhs, Vector3 rhs)"),
            Method("Cross", "Vector3 Cross(Vector3 lhs, Vector3 rhs)"),
            Method("Lerp", "Vector3 Lerp(Vector3 a, Vector3 b, float t)"),
            Method("MoveTowards", "Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta)")
        );

        // Vector2
        RegisterType("Vector2",
            Field("x", "float x"),
            Field("y", "float y"),
            Field("normalized", "Vector2 normalized"),
            Field("magnitude", "float magnitude"),
            Field("sqrMagnitude", "float sqrMagnitude"),
            Field("zero", "Vector2 zero"),
            Field("one", "Vector2 one"),
            Field("up", "Vector2 up"),
            Field("down", "Vector2 down"),
            Field("left", "Vector2 left"),
            Field("right", "Vector2 right"),
            Method("Distance", "float Distance(Vector2 a, Vector2 b)"),
            Method("Dot", "float Dot(Vector2 lhs, Vector2 rhs)"),
            Method("Lerp", "Vector2 Lerp(Vector2 a, Vector2 b, float t)")
        );

        // Quaternion
        RegisterType("Quaternion",
            Field("x", "float x"),
            Field("y", "float y"),
            Field("z", "float z"),
            Field("w", "float w"),
            Field("eulerAngles", "Vector3 eulerAngles"),
            Field("identity", "Quaternion identity"),
            Method("Euler", "Quaternion Euler(float x, float y, float z)"),
            Method("LookRotation", "Quaternion LookRotation(Vector3 forward)"),
            Method("Slerp", "Quaternion Slerp(Quaternion a, Quaternion b, float t)"),
            Method("Angle", "float Angle(Quaternion a, Quaternion b)"),
            Method("Inverse", "Quaternion Inverse(Quaternion rotation)")
        );

        // Mathf
        RegisterType("Mathf",
            Field("PI", "float PI"),
            Field("Infinity", "float Infinity"),
            Field("Deg2Rad", "float Deg2Rad"),
            Field("Rad2Deg", "float Rad2Deg"),
            Field("Epsilon", "float Epsilon"),
            Method("Sin", "float Sin(float f)"),
            Method("Cos", "float Cos(float f)"),
            Method("Tan", "float Tan(float f)"),
            Method("Sqrt", "float Sqrt(float f)"),
            Method("Abs", "float Abs(float f)"),
            Method("Min", "float Min(float a, float b)"),
            Method("Max", "float Max(float a, float b)"),
            Method("Clamp", "float Clamp(float value, float min, float max)"),
            Method("Clamp01", "float Clamp01(float value)"),
            Method("Lerp", "float Lerp(float a, float b, float t)"),
            Method("RoundToInt", "int RoundToInt(float f)"),
            Method("FloorToInt", "int FloorToInt(float f)"),
            Method("CeilToInt", "int CeilToInt(float f)")
        );

        // Time
        RegisterType("Time",
            Field("time", "float time"),
            Field("deltaTime", "float deltaTime"),
            Field("fixedDeltaTime", "float fixedDeltaTime"),
            Field("timeScale", "float timeScale"),
            Field("frameCount", "int frameCount"),
            Field("realtimeSinceStartup", "float realtimeSinceStartup")
        );

        // Debug
        RegisterType("Debug",
            Method("Log", "void Log(object message)"),
            Method("LogWarning", "void LogWarning(object message)"),
            Method("LogError", "void LogError(object message)"),
            Method("LogException", "void LogException(Exception exception)"),
            Method("DrawLine", "void DrawLine(Vector3 start, Vector3 end, Color color)"),
            Method("DrawRay", "void DrawRay(Vector3 start, Vector3 dir, Color color)"),
            Method("Break", "void Break()"),
            Method("Assert", "void Assert(bool condition)")
        );

        // Physics
        RegisterType("Physics",
            Field("gravity", "Vector3 gravity"),
            Method("Raycast", "bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hitInfo)"),
            Method("RaycastAll", "RaycastHit[] RaycastAll(Vector3 origin, Vector3 direction)"),
            Method("OverlapSphere", "Collider[] OverlapSphere(Vector3 position, float radius)"),
            Method("OverlapBox", "Collider[] OverlapBox(Vector3 center, Vector3 halfExtents)")
        );

        // Component
        RegisterType("Component",
            Field("gameObject", "GameObject gameObject"),
            Field("transform", "Transform transform"),
            Field("tag", "string tag"),
            Method("GetComponent", "T GetComponent<T>()"),
            Method("GetComponents", "T[] GetComponents<T>()")
        );

        // MonoBehaviour
        RegisterType("MonoBehaviour",
            Field("enabled", "bool enabled"),
            Field("isActiveAndEnabled", "bool isActiveAndEnabled"),
            Field("gameObject", "GameObject gameObject"),
            Field("transform", "Transform transform"),
            Method("StartCoroutine", "Coroutine StartCoroutine(IEnumerator routine)"),
            Method("StopCoroutine", "void StopCoroutine(Coroutine routine)"),
            Method("StopAllCoroutines", "void StopAllCoroutines()"),
            Method("Invoke", "void Invoke(string methodName, float time)"),
            Method("CancelInvoke", "void CancelInvoke()")
        );

        // Color
        RegisterType("Color",
            Field("r", "float r"),
            Field("g", "float g"),
            Field("b", "float b"),
            Field("a", "float a"),
            Field("red", "Color red"),
            Field("green", "Color green"),
            Field("blue", "Color blue"),
            Field("white", "Color white"),
            Field("black", "Color black"),
            Field("yellow", "Color yellow"),
            Field("cyan", "Color cyan"),
            Field("magenta", "Color magenta"),
            Field("gray", "Color gray"),
            Field("clear", "Color clear"),
            Method("Lerp", "Color Lerp(Color a, Color b, float t)")
        );
    }

    public static bool TryGetMembers(string typeName, out List<Symbol> members)
    {
        return _unityTypes.TryGetValue(typeName, out members!);
    }

    public static bool IsKnownUnityType(string typeName) =>
        _unityTypes.ContainsKey(typeName);

    private static void RegisterType(string typeName, params Symbol[] symbols)
    {
        var list = new List<Symbol>(symbols.Length);
        foreach (var s in symbols)
        {
            list.Add(s with { ContainingType = typeName });
        }
        _unityTypes[typeName] = list;
    }

    private static Symbol Method(string name, string signature) =>
        new(0, 0, name, SymbolKind.Method, "", default, signature, null, false);

    private static Symbol Field(string name, string signature) =>
        new(0, 0, name, SymbolKind.Field, "", default, signature, null, false);
}
