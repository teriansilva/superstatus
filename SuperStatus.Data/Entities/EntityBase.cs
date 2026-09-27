using System.ComponentModel.DataAnnotations;

namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Base class for all entities
    /// </summary>
    public class EntityBase
    {
        [Key]
        public long Id { get; set; }

    }
}
