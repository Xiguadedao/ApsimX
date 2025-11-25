using APSIM.Core;
using APSIM.Numerics;
using APSIM.Shared.Utilities;
using Models.Core;
using Models.Functions;
using System;
using System.Collections.Generic;
using System.Linq;
using Models.Interfaces;

namespace Models.Soils.Nutrients
{

    /// <summary>
    /// Encapsulates a carbon and nutrient flow between pools.  This flow is characterised in terms of the rate of flow (fraction of the pool per day).
    /// Carbon loss as CO2 is expressed in terms of the efficiency of C retension within the soil.
    /// </summary>
    [Serializable]
    [ValidParent(ParentType = typeof(OrganicPool))]
    [PresenterName("UserInterface.Presenters.PropertyPresenter")]
    [ViewName("UserInterface.Views.PropertyView")]
    public class OrganicFlow : Model, IStructureDependency
    {
        /// <summary>Structure instance supplied by APSIM.core.</summary>
        [field: NonSerialized]
        public IStructure Structure { private get; set; }


        private IOrganicPool[] destinations;
        private double[] carbonFlowToDestination;
        private double[] nitrogenFlowToDestination;
        private double[] phosphorusFlowToDestination;
        private double[] mineralisedN;
        private double[] mineralisedP;
        private double[] catm;
        private double co2EfficiencyValue;


        [Link(Type = LinkType.Child, ByName = true)]
        private readonly IFunction rate = null;

        [Link(ByName = true)]
        private readonly IFunction co2Efficiency = null;

        [Link(ByName = true)]
        private readonly ISolute no3 = null;

        [Link(ByName = true)]
        private readonly ISolute nh4 = null;

        [Link(ByName = true, IsOptional = true)]
        private readonly ISolute labileP = null;

        [Link(ByName = true, IsOptional = true)]
        private readonly IWeather weather = null;

        [Link(ByName = true, IsOptional = true)]
        private readonly IClock clock = null;

        // 1) 在现有 [Link] 字段附近（Link 列表后）添加 summary 链接：
        [Link]
        private ISummary summary = null;

        private int cachedMatYear = int.MinValue;

        /// <summary>Names of destination pools</summary>
        [Description("Names of destination pools (comma separated)")]
        public string[] DestinationNames { get; set; }

        /// <summary>Fractions for each destination pool</summary>
        [Description("Fractions of flow to each pool (comma separated)")]
        public double[] DestinationFraction { get; set; }


        /// <summary>Amount of N Mineralised (kg/ha)</summary>
        public IReadOnlyList<double> MineralisedN => mineralisedN;

        /// <summary>Amount of P Mineralised (kg/ha)</summary>
        public IReadOnlyList<double> MineralisedP => mineralisedP;

        /// <summary>Total carbon lost to the atmosphere (kg/ha)</summary>
        public IReadOnlyList<double> Catm => catm;

        private double Co2EfficiencyFromMAT(double mat)
        {
            if (double.IsNaN(mat)) return co2Efficiency?.Value() ?? 0.0;
            if (mat < 1.3) return 0.0064 * mat + 0.45;
            if (mat <= 16.5) return -0.004 * mat + 0.46;
            return 0.025 * mat + 0.037;
        }

        /// <summary>Performs the initial checks and setup</summary>
        /// <param name="numberLayers">Number of layers.</param>
        public void Initialise(int numberLayers)
        {
            mineralisedN = new double[numberLayers];
            mineralisedP = new double[numberLayers];
            catm = new double[numberLayers];
            carbonFlowToDestination = new double[DestinationNames.Length];
            nitrogenFlowToDestination = new double[DestinationNames.Length];
            phosphorusFlowToDestination = new double[DestinationNames.Length];
            //co2EfficiencyValue = co2Efficiency.Value();
            if (weather != null)
            {
                int year = (clock != null) ? clock.Today.Year : DateTime.Now.Year;
                co2EfficiencyValue = Co2EfficiencyFromMAT(weather.MAT); // 这里把 weather.MAT 传给 mat 参数
                cachedMatYear = year;
            }
            else
                co2EfficiencyValue = co2Efficiency.Value();
        }

        /// <summary>Perform daily flow calculations.</summary>
        public void DoFlow()
        {
            if (destinations == null)
            {
                destinations = new IOrganicPool[DestinationNames.Length];
                for (int i = 0; i < DestinationNames.Length; i++)
                {
                    IOrganicPool destination = Structure.Find<IOrganicPool>(DestinationNames[i]);
                    if (destination == null)
                        throw new Exception("Cannot find destination pool with name: " + DestinationNames[i]);
                    destinations[i] = destination;
                }
            }
            summary?.WriteMessage(this, $"OrganicFlow debug: weather={(weather != null)}, cachedMatYear={cachedMatYear}, MAT={(weather != null ? weather.MAT : double.NaN):F3}, co2Func={co2Efficiency?.Value():F3}, usedCo2Eff={co2EfficiencyValue:F3}", MessageType.Diagnostic);
            // 年份变更检查：只有当年份与缓存不同时才重新用 MAT 计算 co2EfficiencyValue
            if (weather != null)
            {
                int currentYear = (clock != null) ? clock.Today.Year : DateTime.Now.Year;
                if (currentYear != cachedMatYear)
                {
                    double currentYearMAT = weather.MAT;
                    co2EfficiencyValue = Co2EfficiencyFromMAT(currentYearMAT);
                    cachedMatYear = currentYear;
                }
            }

            OrganicPool source = Parent as OrganicPool;

            int numLayers = source.C.Count;

            double[] no3 = this.no3.kgha;
            double[] nh4 = this.nh4.kgha;
            double[] labileP;
            if (this.labileP == null)
                labileP = new double[numLayers];
            else
                labileP = this.labileP.kgha;

            int numDestinations = destinations.Length;
            for (int i = 0; i < numLayers; i++)
            {

                double carbonFlowFromSource = rate.Value(i) * source.C[i];
                double nitrogenFlowFromSource = MathUtilities.Divide(carbonFlowFromSource * source.N[i], source.C[i], 0);
                double phosphorusFlowFromSource = MathUtilities.Divide(carbonFlowFromSource * source.P[i], source.C[i], 0);

                double totalNitrogenFlowToDestinations = 0;
                double totalPhosphorusFlowToDestinations = 0;

                for (int j = 0; j < numDestinations; j++)
                {
                    var destination = destinations[j];
                    carbonFlowToDestination[j] = carbonFlowFromSource * co2EfficiencyValue * DestinationFraction[j];
                    nitrogenFlowToDestination[j] = MathUtilities.Divide(carbonFlowToDestination[j] * destination.N[i], destination.C[i], 0.0);
                    totalNitrogenFlowToDestinations += nitrogenFlowToDestination[j];
                    phosphorusFlowToDestination[j] = MathUtilities.Divide(carbonFlowToDestination[j] * destination.P[i], destination.C[i], 0.0);
                    totalPhosphorusFlowToDestinations += phosphorusFlowToDestination[j];

                }

                // some pools do not fully occupy a layer (e.g. residue decomposition) and so need to incorporate fraction of layer
                double mineralNSupply = (no3[i] + nh4[i]) * source.LayerFraction[i];
                double nSupply = nitrogenFlowFromSource + mineralNSupply;
                double mineralPSupply = labileP[i] * source.LayerFraction[i];
                double pSupply = phosphorusFlowFromSource + mineralPSupply;

                double SupplyFactor = 1;

                if (totalNitrogenFlowToDestinations > nSupply)
                    SupplyFactor = MathUtilities.Bound(MathUtilities.Divide(mineralNSupply, totalNitrogenFlowToDestinations - nitrogenFlowFromSource, 1.0), 0.0, 1.0);
                if (totalPhosphorusFlowToDestinations > pSupply)
                {
                    double pSupplyFactor = MathUtilities.Bound(MathUtilities.Divide(mineralPSupply, totalPhosphorusFlowToDestinations - phosphorusFlowFromSource, 1.0), 0.0, 1.0);
                    // ALERT
                    pSupplyFactor = 1; // remove P constraint until P model fully operational
                    SupplyFactor = Math.Min(SupplyFactor, pSupplyFactor);
                }

                if (SupplyFactor < 1)
                {
                    for (int j = 0; j < numDestinations; j++)
                    {
                        carbonFlowToDestination[j] *= SupplyFactor;
                        nitrogenFlowToDestination[j] *= SupplyFactor;
                        phosphorusFlowToDestination[j] *= SupplyFactor;
                    }
                    totalNitrogenFlowToDestinations *= SupplyFactor;
                    totalPhosphorusFlowToDestinations *= SupplyFactor;

                    carbonFlowFromSource *= SupplyFactor;
                    nitrogenFlowFromSource *= SupplyFactor;
                    phosphorusFlowFromSource *= SupplyFactor;

                }

                // Remove from source
                source.Add(i, -carbonFlowFromSource, -nitrogenFlowFromSource, -phosphorusFlowFromSource);

                catm[i] = carbonFlowFromSource - carbonFlowToDestination.Sum();

                // Add to destination
                for (int j = 0; j < numDestinations; j++)
                    destinations[j].Add(i, carbonFlowToDestination[j], nitrogenFlowToDestination[j], phosphorusFlowToDestination[j]);

                if (totalNitrogenFlowToDestinations <= nitrogenFlowFromSource)
                {
                    mineralisedN[i] = nitrogenFlowFromSource - totalNitrogenFlowToDestinations;
                    nh4[i] += MineralisedN[i];
                }
                else
                {
                    double NDeficit = totalNitrogenFlowToDestinations - nitrogenFlowFromSource;
                    double NH4Immobilisation = Math.Min(nh4[i], NDeficit);
                    nh4[i] -= NH4Immobilisation;
                    NDeficit -= NH4Immobilisation;

                    double NO3Immobilisation = Math.Min(no3[i], NDeficit);
                    no3[i] -= NO3Immobilisation;
                    NDeficit -= NO3Immobilisation;

                    mineralisedN[i] = -NH4Immobilisation - NO3Immobilisation;

                    if (MathUtilities.IsGreaterThan(NDeficit, 0.0))
                        throw new Exception("Insufficient mineral N for immobilisation demand for C flow " + Name);
                }

                if (totalPhosphorusFlowToDestinations <= phosphorusFlowFromSource)
                {
                    mineralisedP[i] = phosphorusFlowFromSource - totalPhosphorusFlowToDestinations;
                    labileP[i] += MineralisedP[i];
                }
                else
                {
                    double PDeficit = totalPhosphorusFlowToDestinations - phosphorusFlowFromSource;
                    double PImmobilisation = Math.Min(labileP[i], PDeficit);
                    labileP[i] -= PImmobilisation;
                    PDeficit -= PImmobilisation;
                    mineralisedP[i] = -PImmobilisation;

                    // ALERT
                    //if (MathUtilities.IsGreaterThan(PDeficit, 0.0))
                    //    throw new Exception("Insufficient mineral P for immobilisation demand for C flow " + Name);
                }
            }
        }
    }
}