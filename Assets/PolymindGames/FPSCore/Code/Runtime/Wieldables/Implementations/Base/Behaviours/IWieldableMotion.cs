using PolymindGames.ProceduralMotion;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    public interface IWieldableMotion
    {
        MotionComponents HeadComponents { get; }
        MotionComponents HandsComponents { get; }
        
        /// <summary>
        /// Gets or sets the position offset of the wieldable motion.
        /// </summary>
        Vector3 PositionOffset { get; set; }
        
        /// <summary>
        /// Gets or sets the rotation offset of the wieldable motion.
        /// </summary>
        Vector3 RotationOffset { get; set; }

        /// <summary>
        /// Sets the motion profile of the wieldable motion.
        /// </summary>
        /// <param name="profile">The motion profile to set.</param>
        void SetProfile(MotionProfile profile);
    }
}