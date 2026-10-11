using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class DataProtectionKeyConfiguration
    : IEntityTypeConfiguration<DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<DataProtectionKey> builder)
    {
        builder.ToTable("data_protection_keys");

        builder.HasKey(key => key.Id);

        builder.Property(key => key.Id).HasColumnName("id");
        builder.Property(key => key.FriendlyName)
            .HasColumnName("friendly_name")
            .HasColumnType("text");
        builder.Property(key => key.Xml)
            .HasColumnName("xml")
            .HasColumnType("text");
    }
}
