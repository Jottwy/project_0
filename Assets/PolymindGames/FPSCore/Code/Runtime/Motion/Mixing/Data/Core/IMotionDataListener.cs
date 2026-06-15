using System;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Interface for listening to updates on motion data changes.
    /// </summary>
    public interface IMotionDataListener
    {
        /// <summary>
        /// Gets the type of motion data this listener is interested in.
        /// </summary>
        Type MotionType { get; }

        /// <summary>
        /// Updates the listener with the latest motion data.
        /// </summary>
        /// <param name="data">The updated motion data.</param>
        void UpdateData(MotionData data);
    }
}