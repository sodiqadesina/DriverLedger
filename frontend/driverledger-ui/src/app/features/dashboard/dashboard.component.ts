import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';

type ActionItem = { title: string; subtitle: string; tone: 'warn'|'danger' };

@Component({
  selector: 'dl-dashboard',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './dashboard.component.html',
})
export class DashboardComponent {
  readiness = 78;

  actions: ActionItem[] = [
    { title: '3 receipts need review', subtitle: 'Items are on HOLD pending classification', tone: 'warn' },
    { title: 'Missing statements for November', subtitle: 'Upload Uber and Lyft statements', tone: 'warn' },
    { title: 'Q4 GST/HST deadline approaching', subtitle: 'Due in 23 days', tone: 'danger' },
  ];

  kpis = [
    { label: 'Gross Revenue', value: '$68,450.00', delta: '+12.3% vs last period' },
    { label: 'Expenses', value: '$24,180.00', delta: '+5.2% vs last period' },
    { label: 'Net Profit', value: '$44,270.00', delta: '+15.7% vs last period' },
    { label: 'Estimated GST Owing', value: '$5,746.50', delta: '+8.9% vs last period' },
  ];

  activity = [
    { title: 'Receipt uploaded', meta: '2 minutes ago', detail: 'Gas station receipt • $65.42' },
    { title: 'Statement processed', meta: '1 hour ago', detail: 'Uber November 2024 • $4,250.00' },
    { title: 'Mileage logged', meta: '3 hours ago', detail: '245 km business use' },
    { title: 'AI categorized 12 items', meta: 'Yesterday', detail: 'All receipts approved' },
  ];
}
