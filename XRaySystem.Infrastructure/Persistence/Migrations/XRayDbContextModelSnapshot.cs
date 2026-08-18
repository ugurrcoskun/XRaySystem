using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace XRaySystem.Infrastructure.Persistence.Migrations;

[DbContext(typeof(XRayDbContext))]
public sealed class XRayDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "8.0.0");

        modelBuilder.Entity("XRaySystem.Domain.Entities.XRayMeasurement", entity =>
        {
            entity.Property<Guid>("Id").HasColumnType("TEXT");
            entity.Property<string>("ImagePath").HasColumnType("TEXT");
            entity.Property<bool>("IsSimulator").HasColumnType("INTEGER");
            entity.Property<int>("Profile").HasColumnType("INTEGER");
            entity.Property<DateTime>("Timestamp").HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.ToTable("Measurements");

            entity.OwnsOne("XRaySystem.Domain.ValueObjects.SensorReadings", "Readings", owned =>
            {
                owned.Property<Guid>("XRayMeasurementId").HasColumnType("TEXT");
                owned.Property<double>("ExposureTime")
                    .HasColumnType("REAL").HasColumnName("Readings_ExposureTime");
                owned.Property<double>("TubeCurrent")
                    .HasColumnType("REAL").HasColumnName("Readings_TubeCurrent");
                owned.Property<double>("Voltage")
                    .HasColumnType("REAL").HasColumnName("Readings_Voltage");
                owned.Property<double>("XRayIntensity")
                    .HasColumnType("REAL").HasColumnName("Readings_XRayIntensity");
                owned.HasKey("XRayMeasurementId");
                owned.ToTable("Measurements");
                owned.WithOwner().HasForeignKey("XRayMeasurementId");
            });

            entity.Navigation("Readings").IsRequired();
        });
#pragma warning restore 612, 618
    }
}
