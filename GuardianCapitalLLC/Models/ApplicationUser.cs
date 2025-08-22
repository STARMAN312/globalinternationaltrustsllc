﻿using Microsoft.AspNetCore.Identity;


namespace globalinternationaltrusts.Models
{
    public class ApplicationUser : IdentityUser
    {
        public string? FullName { get; set; }
        public string? Address { get; set; }
        public string? WorkPhone { get; set; }
        public string? PersonalPhone { get; set; }
        public string? WorkEmail { get; set; }
        public string? PersonalEmail { get; set; }
        public virtual ICollection<BankAccount>? BankAccounts { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool IsBanned { get; set; } = false;
        public string? BanReason { get; set; }

    }
}
