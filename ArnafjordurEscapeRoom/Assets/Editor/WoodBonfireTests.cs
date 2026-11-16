using System.Collections.Generic;
using System.Reflection;
using Arnafjordur.Characters;
using NUnit.Framework;
using UnityEngine;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// EditMode tests verifying firewood ignition, 5-minute-per-wood burn durations,
    /// bonfire stacking via drop/use, and adding wood to an active fire.
    /// </summary>
    public sealed class WoodBonfireTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _created.Count; i++)
            {
                if (_created[i] != null)
                {
                    DeactivateInEditMode(_created[i]);
                    Object.DestroyImmediate(_created[i]);
                }
            }

            _created.Clear();

            var worldItems = GameObject.Find("_WorldItems");
            if (worldItems != null)
            {
                DeactivateInEditMode(worldItems);
                Object.DestroyImmediate(worldItems);
            }

            foreach (WoodBonfire b in Object.FindObjectsByType<WoodBonfire>(FindObjectsSortMode.None))
            {
                if (b != null)
                {
                    DeactivateInEditMode(b.gameObject);
                    Object.DestroyImmediate(b.gameObject);
                }
            }
        }

        private Firewood CreateWood(string name, Vector3 position, bool isLog)
        {
            GameObject go = GameObject.CreatePrimitive(isLog ? PrimitiveType.Cylinder : PrimitiveType.Cube);
            go.name = name;
            go.transform.position = position;
            go.transform.localScale = isLog ? new Vector3(0.10f, 0.45f, 0.10f) : new Vector3(0.16f, 0.03f, 0.90f);
            go.transform.rotation = isLog ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
            _created.Add(go);

            var pickup = go.AddComponent<PickupItem>();
            pickup.Configure(isLog ? "Driftwood" : "Plank", 0f, isLog ? 0.05f : 0.015f, false, "Stack", false);
            pickup.SetTwoHanded(true);

            var wood = go.AddComponent<Firewood>();
            wood.Configure(Firewood.DefaultBurnSeconds);
            ActivateInEditMode(go);
            return wood;
        }

        private CharacterItemHolder CreateCharacterWithHand(Vector3 position)
        {
            var root = new GameObject("TestCharacter");
            root.transform.position = position;
            _created.Add(root);

            var hand = new GameObject("RightHand");
            hand.transform.SetParent(root.transform, false);
            hand.transform.localPosition = new Vector3(0.25f, 1.0f, 0.25f);

            var holder = root.AddComponent<CharacterItemHolder>();
            holder.Configure(hand.transform, Vector3.zero);
            ActivateInEditMode(root);
            return holder;
        }

        private Lighter CreateLighter()
        {
            var go = new GameObject("TestLighter");
            _created.Add(go);
            var flame = new GameObject("Flame");
            flame.transform.SetParent(go.transform, false);
            var light = flame.AddComponent<Light>();
            go.AddComponent<PickupItem>().Configure("Lighter", 0.02f, 0.04f, true, "Light", false);
            var lighter = go.AddComponent<Lighter>();
            lighter.Configure(flame, light);
            ActivateInEditMode(go);
            return lighter;
        }

        private static void ActivateInEditMode(GameObject go)
        {
            foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                InvokeLifecycle(mb, "Awake");
                InvokeLifecycle(mb, "OnEnable");
            }
        }

        private static void DeactivateInEditMode(GameObject go)
        {
            foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                InvokeLifecycle(mb, "OnDisable");
            }
        }

        private static void InvokeLifecycle(MonoBehaviour mb, string methodName)
        {
            if (mb == null) return;
            MethodInfo mi = mb.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            mi?.Invoke(mb, null);
        }

        [Test]
        public void SingleWood_IgnitedByLighter_LastsFiveMinutes()
        {
            Firewood wood = CreateWood("Log1", new Vector3(0f, 0.05f, 1.0f), isLog: true);
            CharacterItemHolder holder = CreateCharacterWithHand(Vector3.zero);
            Lighter lighter = CreateLighter();
            Assert.IsTrue(holder.TryPickup(lighter.GetComponent<PickupItem>()));

            lighter.OnUseItem(holder, lighter.GetComponent<PickupItem>());

            Assert.IsNotNull(wood.Stack);
            Assert.IsTrue(wood.Stack.IsLit);
            Assert.AreEqual(1, wood.Stack.WoodCount);
            Assert.AreEqual(300f, wood.Stack.RemainingSeconds, 0.01f);
            Assert.IsFalse(wood.Pickup.enabled, "Burning wood must not remain pickable.");
        }

        [Test]
        public void DroppingSecondWoodOnFirst_StacksLikeBonfireAndLastsTenMinutes()
        {
            Firewood baseWood = CreateWood("BaseLog", new Vector3(0f, 0.05f, 0.6f), isLog: true);
            Firewood secondWood = CreateWood("SecondPlank", new Vector3(5f, 0.015f, 5f), isLog: false);
            CharacterItemHolder holder = CreateCharacterWithHand(Vector3.zero);

            Assert.IsTrue(holder.TryPickup(secondWood.Pickup));
            holder.DropHeldItem();

            Assert.IsFalse(holder.HasItem);
            Assert.IsNotNull(baseWood.Stack);
            Assert.AreSame(baseWood.Stack, secondWood.Stack);
            Assert.AreEqual(2, baseWood.Stack.WoodCount);
            Assert.AreEqual(600f, baseWood.Stack.RemainingSeconds, 0.01f);
            Assert.IsFalse(baseWood.Stack.IsLit);
            Assert.Greater(secondWood.transform.position.y, baseWood.transform.position.y,
                "Second stacked piece should sit higher than the base piece.");

            // Light the 2-wood stack and verify 10 minutes burn time.
            Lighter lighter = CreateLighter();
            Assert.IsTrue(holder.TryPickup(lighter.GetComponent<PickupItem>()));
            lighter.OnUseItem(holder, lighter.GetComponent<PickupItem>());

            Assert.IsTrue(baseWood.Stack.IsLit);
            Assert.AreEqual(600f, baseWood.Stack.RemainingSeconds, 0.01f);
        }

        [Test]
        public void StackingMoreWood_ExtendsBurnDurationProportionally_EvenWhileBurning()
        {
            Firewood w1 = CreateWood("Log1", new Vector3(0f, 0.05f, 0.6f), isLog: true);
            Firewood w2 = CreateWood("Log2", new Vector3(4f, 0.05f, 4f), isLog: true);
            Firewood w3 = CreateWood("Plank3", new Vector3(6f, 0.015f, 6f), isLog: false);
            Firewood w4 = CreateWood("Log4", new Vector3(8f, 0.05f, 8f), isLog: true);
            CharacterItemHolder holder = CreateCharacterWithHand(Vector3.zero);

            Assert.IsTrue(holder.TryPickup(w2.Pickup));
            holder.DropHeldItem();
            Assert.AreEqual(600f, w1.Stack.RemainingSeconds, 0.01f);

            // Light at 2 wood (10 min), burn for 60 seconds -> 540s left.
            Lighter lighter = CreateLighter();
            Assert.IsTrue(holder.TryPickup(lighter.GetComponent<PickupItem>()));
            lighter.OnUseItem(holder, lighter.GetComponent<PickupItem>());
            WoodBonfire bonfire = w1.Stack;
            bonfire.TickBurn(60f);
            Assert.AreEqual(540f, bonfire.RemainingSeconds, 0.01f);

            // Stack 3rd and 4th wood onto the active fire -> +300s each (540 + 600 = 1140s).
            Assert.IsTrue(holder.TryPickup(w3.Pickup));
            holder.DropHeldItem();
            Assert.AreEqual(3, bonfire.WoodCount);
            Assert.AreEqual(840f, bonfire.RemainingSeconds, 0.01f);

            Assert.IsTrue(holder.TryPickup(w4.Pickup));
            holder.DropHeldItem();
            Assert.AreEqual(4, bonfire.WoodCount);
            Assert.AreEqual(1140f, bonfire.RemainingSeconds, 0.01f);

            // Burn down completely -> fire stops and wood is consumed.
            bonfire.TickBurn(1140f);
            Assert.IsFalse(bonfire.IsLit);
            Assert.IsTrue(bonfire.IsBurnedOut);
            Assert.AreEqual(0, bonfire.WoodCount);
        }

        [Test]
        public void PickingUpFromUnlitTwoWoodBonfire_DissolvesStackCleanly()
        {
            Firewood w1 = CreateWood("Log1", new Vector3(0f, 0.05f, 0.6f), isLog: true);
            Firewood w2 = CreateWood("Log2", new Vector3(4f, 0.05f, 4f), isLog: true);
            CharacterItemHolder holder = CreateCharacterWithHand(Vector3.zero);

            Assert.IsTrue(holder.TryPickup(w2.Pickup));
            holder.DropHeldItem();
            WoodBonfire stack = w1.Stack;
            Assert.IsNotNull(stack);
            Assert.AreEqual(2, stack.WoodCount);

            // Pick w2 back off the unlit stack -> stack dissolves, w1 remains loose on ground.
            Assert.IsTrue(holder.TryPickup(w2.Pickup));
            Assert.IsNull(w1.Stack);
            Assert.IsNull(w2.Stack);
            Assert.IsTrue(stack == null);
            Assert.IsTrue(w1.Pickup.enabled);
        }

        [Test]
        public void DroppingSingleLogAlone_LiesHorizontallyOnGround()
        {
            Firewood log = CreateWood("LoneLog", new Vector3(4f, 0.05f, 4f), isLog: true);
            CharacterItemHolder holder = CreateCharacterWithHand(Vector3.zero);

            Assert.IsTrue(holder.TryPickup(log.Pickup));
            holder.DropHeldItem();

            // Log's long axis is local +Y; when lying flat on ground, transform.up.y should be ~0.
            Assert.Less(Mathf.Abs(log.transform.up.y), 0.05f, "Dropped driftwood log should lie flat, not stand upright.");
        }
    }
}
