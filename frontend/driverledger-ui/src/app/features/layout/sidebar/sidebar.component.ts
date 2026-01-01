import { Component, EventEmitter, Output } from '@angular/core';
import { RouterModule } from '@angular/router';
import { CommonModule } from '@angular/common';
import { NavBadgeComponent } from '../../../shared/ui/nav-badge/nav-badge.component';

type NavItem = {
  label: string;
  route: string;
  icon?: string;
  badgeText?: string;
  badgeCount?: number;
};

@Component({
  selector: 'dl-sidebar',
  standalone: true,
  imports: [RouterModule, CommonModule, NavBadgeComponent],
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.css',
})
export class SidebarComponent {
  @Output() ai = new EventEmitter<void>();

  items: NavItem[] = [
    { label: 'Dashboard', route: '/app/dashboard' },
    { label: 'Live Statement', route: '/app/live-statement', badgeText: 'Core' },
    { label: 'Receipts', route: '/app/receipts', badgeCount: 3 },
    { label: 'Income Hub', route: '/app/income-hub' },           // placeholder route if you want it
    { label: 'Reconciliation', route: '/app/reconciliation' },   // placeholder route if you want it
    { label: 'Mileage', route: '/app/mileage' },                 // placeholder route if you want it
    { label: 'GST/HST Center', route: '/app/gst-hst' },          // placeholder route if you want it
    { label: 'Analytics', route: '/app/analytics' },             // placeholder route if you want it
    { label: 'Exports & Audit', route: '/app/exports-audit' },   // placeholder route if you want it
    { label: 'Processing Queue', route: '/app/processing' },     // placeholder route if you want it
    { label: 'Policies', route: '/app/policies' },               // placeholder route if you want it
    { label: 'Notifications', route: '/app/notifications', badgeCount: 5 },
    { label: 'Audit Log', route: '/app/audit-log' },             // placeholder route if you want it
  ];
}
