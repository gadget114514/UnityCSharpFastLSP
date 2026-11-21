namespace UnityCSharpFastLSP.Semantics;

public static class UnityRules
{
    private static readonly Dictionary<string, string> LifecycleDocs = new(StringComparer.Ordinal)
    {
        ["Awake"] = "Called when the script instance is being loaded.",
        ["Start"] = "Called on the frame when a script is enabled just before any of the Update methods are called the first time.",
        ["Update"] = "Called every frame, if the MonoBehaviour is enabled.",
        ["FixedUpdate"] = "Frame-rate independent MonoBehaviour.FixedUpdate message for physics calculations.",
        ["LateUpdate"] = "Called once per frame, after all Update functions have completed.",
        ["OnEnable"] = "Called when the object becomes enabled and active.",
        ["OnDisable"] = "Called when the behaviour becomes disabled or inactive.",
        ["OnDestroy"] = "Called when MonoBehaviour will be destroyed.",
        ["OnTriggerEnter"] = "When a GameObject collides with another GameObject, Unity calls OnTriggerEnter.",
        ["OnTriggerExit"] = "Called when the Collider other has stopped touching the trigger.",
        ["OnCollisionEnter"] = "Called when this collider/rigidbody has begun touching another rigidbody/collider.",
        ["OnGUI"] = "Called for rendering and handling GUI events.",
    };

    public static readonly string[] LifecycleNames =
    [
        "Awake", "Start", "Update", "FixedUpdate", "LateUpdate",
        "OnEnable", "OnDisable", "OnDestroy", "OnGUI",
        "OnTriggerEnter", "OnTriggerExit", "OnCollisionEnter", "OnCollisionExit"
    ];

    public static readonly string[] CommonAttributes =
    [
        "SerializeField", "HideInInspector", "RequireComponent",
        "Header", "Tooltip", "Range", "Space", "DisallowMultipleComponent",
        "ExecuteInEditMode", "CreateAssetMenu", "MenuItem"
    ];

    public static bool TryGetLifecycleDoc(string name, out string? doc) =>
        LifecycleDocs.TryGetValue(name, out doc);
}
