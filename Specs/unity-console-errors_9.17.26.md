# Unity Console Error Report

*2birds — 2026-09-17*

## Summary

| Severity | Count | Blocks build? |
|---|---|---|
| Compilation error (CS0619) | 14 | Yes |
| Runtime exception | 2 | No (crashes at runtime) |
| Deprecation warning (CS0618) | ~20 | No |

---

## Compilation Errors — CS0619 (blocking)

Unity 6000.5 removed `GetInstanceID()` and `ModifiableContactPair.colliderInstanceID` / `otherColliderInstanceID`. These are hard errors that prevent compilation.

### GetInstanceID() → GetEntityId()

| File | Lines | Call |
|---|---|---|
| `WorldItem.Birds.cs` | 30, 44 | `collision.collider.GetInstanceID()` |
| `WorldItem.cs` | 93 | `impactSphere.GetInstanceID()` |
| `BirdRegistry.Physics.cs` | 58, 59, 74, 75, 130, 173 | `shape.Collider.GetInstanceID()` |

**Fix:** Replace `.GetInstanceID()` with `.GetEntityId()`. `GetEntityId()` returns `EntityId` (a struct), not `int`. All dictionaries keyed on collider instance IDs (`shapeLives`, `colliderShapes`, `touchingBirds`, `suppressedBirds`, `physicalRocks`, `suppressedRockPairs`, `rockContacts`) must change their key type from `int` to `EntityId`. The field `birdColliderId` in `WorldItem.Birds.cs` must also become `EntityId`.

### ModifiableContactPair instance IDs → entity IDs

| File | Lines | Old | New |
|---|---|---|---|
| `BirdRegistry.Physics.cs` | 142 | `pair.colliderInstanceID` | `pair.colliderEntityId` |
| `BirdRegistry.Physics.cs` | 143, 145 | `pair.otherColliderInstanceID` | `pair.otherColliderEntityId` |
| `BirdRegistry.Physics.cs` | 144, 145 | `pair.colliderInstanceID` | `pair.colliderEntityId` |

**Fix:** Rename each property access. Return type changes from `int` to `EntityId`, consistent with the dictionary key migration above.

---

## Runtime Errors

### NullReferenceException in MenuPresenter.Bind() — line 27

```
NullReferenceException: Object reference not set to an instance of an object
  at TwoBirds.MenuPresenter.Bind () in MenuPresenter.cs:27
  at TwoBirds.MenuPresenter.Start () in MenuPresenter.cs:21
```

Line 27 is `session = SessionController.Instance;`. `SessionController.Instance` is null when `MenuPresenter.Start()` runs — likely because `SessionBootstrap.Awake()` hasn't executed yet, or the static constructor crash below prevents it.

**Fix:** Guard `Bind()` — if `SessionController.Instance` is null, defer until it's available (e.g., check again in `OnEnable` or wait a frame).

### UnityException in SessionBootstrap static constructor — line 10

```
UnityException: GetBool is not allowed to be called from a MonoBehaviour
constructor (or instance field initializer), call it in Awake or Start instead.
  at UnityEditor.EditorPrefs.GetBool(...)
  at TwoBirds.SessionBootstrap..cctor() in SessionBootstrap.cs:10
Rethrow as TypeInitializationException
```

The static property `LocalNetworking` calls `EditorPrefs.GetBool` via a `??=` expression body. When Unity touches the `SessionBootstrap` type during deserialization, the static constructor fires too early.

**Fix:** Move the `EditorPrefs` read into `Awake()` so it runs at a safe point in the Unity lifecycle. Set `localNetworking` from there instead of lazily in the property getter.

---

## Deprecation Warnings — CS0618 (non-blocking)

These compile but will become errors in a future Unity version.

### WriteByte / ReadByte → WriteUInt8Unpacked / ReadUInt8Unpacked

| File | Lines |
|---|---|
| `BirdMessages.cs` | 57, 65, 79, 85, 102, 112, 118, 127, 165, 174 |
| `WorldItemMessages.cs` | 76, 94 |

**Fix:** Find-and-replace `WriteByte(` → `WriteUInt8Unpacked(` and `ReadByte()` → `ReadUInt8Unpacked()` in FishNet serializer methods.

### FindObjectsByType(FindObjectsSortMode) → FindObjectsByType\<T\>()

| File | Lines |
|---|---|
| `WorldItemRegistry.cs` | 91, 122 |
| `BirdRegistry.cs` | 101, 115, 123 |
| `BakeTools.cs` (Editor) | 64 |

**Fix:** Remove the `FindObjectsSortMode.None` argument. Call the parameterless `FindObjectsByType<T>()` overload instead.

---

## Recommended Next Steps

1. **Fix compilation errors first** — the `GetInstanceID` → `GetEntityId` migration is the most involved. It touches dictionary key types across `WorldItem.Birds.cs`, `WorldItem.cs`, `BirdRegistry.Physics.cs`, and `BirdRegistry.cs`. Plan for `EntityId` as the key type everywhere collider identity is stored.
2. **Fix runtime errors** — the `SessionBootstrap` static constructor fix unblocks `MenuPresenter` as well (the cascading `TypeInitializationException` likely prevents `SessionController` from initializing).
3. **Clear deprecation warnings** — mechanical find-and-replace, low risk.
