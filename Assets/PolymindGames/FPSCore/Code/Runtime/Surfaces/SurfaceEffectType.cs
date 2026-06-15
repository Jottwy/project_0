namespace PolymindGames.SurfaceSystem
{
	/// <summary>
	/// Represents various types of surface effects caused by interactions such as movement, impact, or environmental events.
	/// </summary>
	public enum SurfaceEffectType
	{
		/// <summary>General collision or contact with a surface.</summary>
		Impact = 0,

		/// <summary>Cutting or slicing effect on a surface.</summary>
		Slash = 1,
		
		/// <summary>Blunt hit or collision with a surface.</summary>
		BluntImpact = 2,
		
		/// <summary>Stabbing or puncturing effect on a surface.</summary>
		Pierce = 3,
		
		/// <summary>Bullet or projectile impact on a surface.</summary>
		BallisticImpact = 4,

		/// <summary>Footstep effect produced by walking.</summary>
		WalkFootstep = 5,

		/// <summary>Footstep effect produced by running.</summary>
		RunFootstep = 6,

		/// <summary>Impact caused by falling onto a surface.</summary>
		FallImpact = 7,

		/// <summary>Grinding or scratching against a surface.</summary>
		Scrape = 8,

		/// <summary>Blast or explosive effect on a surface.</summary>
		Explosion = 9,

		/// <summary>Breaking or fracturing effect on a surface.</summary>
		Shatter = 10,

		/// <summary>Contact with flame or intense heat.</summary>
		FireContact = 11,

		/// <summary>Electrical discharge or sparks on a surface.</summary>
		ElectricDischarge = 12,

		/// <summary>Impact or interaction with water.</summary>
		WaterSplash = 13,

		/// <summary>Interaction with muddy surfaces causing splashes.</summary>
		MudSplash = 14,

		/// <summary>Movement or impact on sandy surfaces.</summary>
		SandDisplacement = 15
	}

	public static class DamageTypeExtensions
	{
		public static SurfaceEffectType GetSurfaceEffectType(this DamageType damageType)
			=> (int)damageType <= (int)DamageType.Ballistic ? (SurfaceEffectType)damageType : SurfaceEffectType.Impact;
	}
}