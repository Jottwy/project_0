using PolymindGames.ProceduralMotion;

namespace PolymindGames
{
    public interface IFPSCharacter : ICharacter
    {
        MotionComponents HeadComponents { get; }
        MotionComponents HandsComponents { get; }
    }
    
    public sealed class MotionComponents
    {
        public IMotionMixer Mixer { get; }
        public IMotionDataHandler Data { get; }
        public IShakeHandler Shake { get; }

        public MotionComponents(IShakeHandler shake, IMotionDataHandler data, IMotionMixer mixer)
        {
            Shake = shake;
            Data = data;
            Mixer = mixer;
        }
    }
}
