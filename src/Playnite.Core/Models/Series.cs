using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable
namespace Playnite.Core.Models
{
    /// <summary>
    /// Describes Series object.
    /// </summary>
    public class Series : DatabaseObject
    {
        /// <summary>
        /// Creates new instance of <see cref="Series"/>.
        /// </summary>
        public Series(Guid id) : base(id)
        {
        }

        /// <summary>
        /// Creates new instance of <see cref="Series"/>.
        /// </summary>
        /// <param name="name"></param>
        public Series(Guid id, string name) : base(id)
        {
            Name = name;
        }

        /// <summary>
        /// Gets empty series.
        /// </summary>
        public static readonly Series Empty = new Series(Guid.Empty, string.Empty);
    }
}
