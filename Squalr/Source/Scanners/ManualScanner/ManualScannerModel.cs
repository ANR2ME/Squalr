﻿namespace Squalr.Source.Scanners.ManualScanner
{
    using ScanConstraints;
    using Snapshots;
    using Squalr.Properties;
    using Squalr.Source.Prefilters;
    using SqualrCore.Source.ActionScheduler;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A memory scanning class for classic manual memory scanning techniques.
    /// </summary>
    internal class ManualScannerModel : ScannerBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ManualScannerModel" /> class.
        /// </summary>
        public ManualScannerModel() : base(
            scannerName: "Manual Scan",
            isRepeated: false,
            dependencyBehavior: new DependencyBehavior(dependencies: typeof(ISnapshotPrefilter)))
        {
            this.ProgressLock = new Object();
        }

        /// <summary>
        /// Gets or sets the snapshot on which we perform the manual scan.
        /// </summary>
        private Snapshot Snapshot { get; set; }

        /// <summary>
        /// Gets or sets the scan constraint manager.
        /// </summary>
        private ScanConstraintManager ScanConstraintManager { get; set; }

        /// <summary>
        /// Gets or sets a lock object for updating scan progress.
        /// </summary>
        private Object ProgressLock { get; set; }

        /// <summary>
        /// Sets the scan constraints for this scan.
        /// </summary>
        /// <param name="scanConstraintManager">The scan constraint manager, which contains all scan constraints.</param>
        public void SetScanConstraintManager(ScanConstraintManager scanConstraintManager)
        {
            this.ScanConstraintManager = scanConstraintManager;
        }

        /// <summary>
        /// Called when the scan begins.
        /// </summary>
        protected override void OnBegin()
        {
            // Initialize snapshot
            this.Snapshot = SnapshotManager.GetInstance().GetActiveSnapshot(createIfNone: true).Clone(this.ScannerName);

            if (this.Snapshot == null || this.ScanConstraintManager == null || this.ScanConstraintManager.Count() <= 0)
            {
                this.Cancel();
                return;
            }
        }

        /// <summary>
        /// Called when the scan updates.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token for handling canceled tasks.</param>
        protected override void OnUpdate(CancellationToken cancellationToken)
        {
            Int32 processedPages = 0;
            Boolean hasRelativeConstraint = this.ScanConstraintManager.HasRelativeConstraint();

            // Determine if we need to increment both current and previous value pointers, or just current value pointers
            PointerIncrementMode pointerIncrementMode;

            if (hasRelativeConstraint)
            {
                pointerIncrementMode = PointerIncrementMode.ValuesOnly;
            }
            else
            {
                pointerIncrementMode = PointerIncrementMode.CurrentOnly;
            }

            // Read memory to get current values for each region
            Parallel.ForEach(
                this.Snapshot.Cast<SnapshotRegion>(),
                SettingsViewModel.GetInstance().ParallelSettingsFullCpu,
                (region) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                region.ReadAllRegionMemory(keepValues: true, readSuccess: out _);
            });

            cancellationToken.ThrowIfCancellationRequested();

            // Enforce each value constraint
            foreach (ScanConstraint scanConstraint in this.ScanConstraintManager)
            {
                this.Snapshot.SetAllValidBits(false);

                Parallel.ForEach(
                    this.Snapshot.Cast<SnapshotRegion>(),
                    SettingsViewModel.GetInstance().ParallelSettingsFullCpu,
                    (region) =>
                {
                    // Ignore region if it requires current & previous values, but we cannot find them
                    if (hasRelativeConstraint && !region.CanCompare())
                    {
                        return;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    for (IEnumerator<SnapshotElementIterator> enumerator = region.IterateElements(pointerIncrementMode, scanConstraint.Constraint, scanConstraint.ConstraintValue);
                        enumerator.MoveNext();)
                    {
                        SnapshotElementIterator element = enumerator.Current;

                        // Perform the comparison based on the current scan constraint
                        if (element.Compare())
                        {
                            element.SetValid(true);
                        }
                    }
                    //// End foreach Element

                    lock (this.ProgressLock)
                    {
                        processedPages++;
                        this.UpdateProgress(processedPages, this.Snapshot.RegionCount, canFinalize: false);
                    }
                });
                //// End foreach Region

                cancellationToken.ThrowIfCancellationRequested();

                this.Snapshot.DiscardInvalidRegions();
            }
            //// End foreach Constraint

            this.UpdateProgress(ScheduledTask.MaximumProgress);
            base.OnUpdate(cancellationToken);
        }

        /// <summary>
        /// Called when the scan ends.
        /// </summary>
        protected override void OnEnd()
        {
            SnapshotManager.GetInstance().SaveSnapshot(this.Snapshot);
            Snapshot = null;

            base.OnEnd();
        }
    }
    //// End class
}
//// End namespace