using System.Linq;
using Arnafjordur.Characters;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// Verifies third-person / top-view shotgun carrying and shouldered poses across all playable characters.
    /// </summary>
    public sealed class DoubleBarrelGunTests
    {
        private const string VillageScenePath = "Assets/Scenes/Arnafjordur_Village.unity";

        [Test]
        public void ThirdPersonCarryAndFirePose_BothHandsReachGrips()
        {
            var scene = EditorSceneManager.OpenScene(VillageScenePath, OpenSceneMode.Single);
            Assert.IsTrue(scene.IsValid());

            CharacterMotor[] motors = Object.FindObjectsByType<CharacterMotor>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(m => m.name).ToArray();
            Assert.GreaterOrEqual(motors.Length, 3);

            DoubleBarrelGun gun = Object.FindObjectsByType<DoubleBarrelGun>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            Assert.IsNotNull(gun);
            var gunItem = gun.GetComponent<PickupItem>();
            Assert.IsNotNull(gunItem);

            Transform rightGrip = FindDeep(gun.transform, "RightGrip");
            Transform leftGrip = FindDeep(gun.transform, "LeftGrip");
            Assert.IsNotNull(rightGrip);
            Assert.IsNotNull(leftGrip);

            foreach (CharacterMotor m in motors)
            {
                var holder = m.GetComponent<CharacterItemHolder>();
                Assert.IsNotNull(holder);
                Assert.IsTrue(holder.TryPickup(gunItem), $"{m.name} failed to pick up gun");

                // 1. Low-ready carry pose
                gun.ClearEffects();
                gun.ApplyThirdPersonPose(holder);

                Vector3 rFist = holder.HandBone.TransformPoint(holder.GripLocal);
                Vector3 lFistLocal = CharacterItemHolder.ComputeFistCentre(m.transform, holder.LeftHandBone, holder.LeftLowerArmBone);
                Vector3 lFist = holder.LeftHandBone.TransformPoint(lFistLocal);

                float rErr = Vector3.Distance(rFist, rightGrip.position);
                float lErr = Vector3.Distance(lFist, leftGrip.position);
                Assert.Less(rErr, 0.025f, $"{m.name} carry right fist missed rightGrip by {rErr:F3}m");
                Assert.Less(lErr, 0.025f, $"{m.name} carry left fist missed leftGrip by {lErr:F3}m");

                // 2. Raised shouldered pose after firing
                gun.Reload(2);
                gun.OnUseItem(holder, gunItem);
                rFist = holder.HandBone.TransformPoint(holder.GripLocal);
                lFist = holder.LeftHandBone.TransformPoint(lFistLocal);
                float rFireErr = Vector3.Distance(rFist, rightGrip.position);
                float lFireErr = Vector3.Distance(lFist, leftGrip.position);
                Assert.Less(rFireErr, 0.025f, $"{m.name} fire right fist missed rightGrip by {rFireErr:F3}m");
                Assert.Less(lFireErr, 0.025f, $"{m.name} fire left fist missed leftGrip by {lFireErr:F3}m");

                holder.ReleaseHeldItem();
                gun.ClearEffects();
            }
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }
    }
}
