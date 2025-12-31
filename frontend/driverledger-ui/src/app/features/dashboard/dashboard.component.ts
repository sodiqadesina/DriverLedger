import { Component } from '@angular/core';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';

@Component({
  selector: 'dl-dashboard',
  standalone: true,
  imports: [PageHeaderComponent],
  template: `
    <dl-page-header title="Dashboard" subtitle="Overview of receipts, ledger and statement." />

    <div class="grid gap-3">
      <div class="border rounded-xl p-4">
        <div class="font-semibold">Receipts</div>
        <div class="text-sm opacity-70">Upload documents and track Processing / HOLD / READY.</div>
      </div>

      <div class="border rounded-xl p-4">
        <div class="font-semibold">Ledger</div>
        <div class="text-sm opacity-70">Read-only projection of posted entries.</div>
      </div>

      <div class="border rounded-xl p-4">
        <div class="font-semibold">Live Statement</div>
        <div class="text-sm opacity-70">Read-only snapshot of financial metrics.</div>
      </div>
    </div>
  `
})
export class DashboardComponent {}
